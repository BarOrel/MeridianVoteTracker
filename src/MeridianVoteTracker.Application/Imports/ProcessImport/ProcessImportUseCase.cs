using MeridianVoteTracker.Application.Abstractions;
using MeridianVoteTracker.Application.Imports.Pipeline;
using MeridianVoteTracker.Application.Imports.Ports;
using MeridianVoteTracker.Application.Votes.Ports;
using MeridianVoteTracker.Domain.Conflicts;
using MeridianVoteTracker.Domain.Imports;
using MeridianVoteTracker.Domain.MemberStates;
using MeridianVoteTracker.Domain.Votes;
using Microsoft.EntityFrameworkCore;

namespace MeridianVoteTracker.Application.Imports.ProcessImport;

/// <summary>
/// Runs one claimed import attempt. Steps are separate and format-independent after the reader:
/// <code>
/// storage → reader (parse) → validator (row rules) → staging (COPY)          [phase Staging, restartable]
///         → per vote batch: classifier → approved data + conflicts + audit
///                           + row classifications + outcomes + progress       [phase Classifying, resumable]
///         → finalize (only after every row is classified)
/// </code>
/// Transactions: one per staging batch, one per classification batch (everything that batch decided commits
/// together or not at all), and a final one that completes the job. A crash between batches loses nothing:
/// staging restarts from the file; classification continues with the votes that still have unclassified rows,
/// and committed classifications are never revisited. Every write verifies the claim is still current.
/// </summary>
public sealed class ProcessImportUseCase(
    IApplicationDbContext db,
    IUnitOfWork unitOfWork,
    IImportFileStorage storage,
    ImportFileReaderResolver readers,
    IImportStagingStore staging,
    IApprovedVoteStore approvedVotes,
    TimeProvider time,
    ImportProcessingSettings settings)
{
    private static readonly ImportClassification[] IssueClassifications =
        [ImportClassification.Invalid, ImportClassification.Contradiction, ImportClassification.Conflict];

    /// <summary>The approved data a classification batch compares against, read under the vote locks.</summary>
    private sealed record ApprovedData(IReadOnlyDictionary<AssemblyVoteId, ApprovedVoteState> Votes, ILookup<int, Conflict> KnownConflicts);

    public async Task ExecuteAsync(ImportClaim claim, CancellationToken cancellationToken)
    {
        var job = await LoadJobAsync(claim.ImportJobId, cancellationToken);
        var membership = await LoadMembershipAsync(cancellationToken);

        // A retry after staging was completed skips straight to classification.
        if (job.Phase == ImportPhase.Staging)
        {
            await StageAsync(job, claim, membership, cancellationToken);
        }

        await ClassifyAsync(job.Id, claim, new VoteImportClassifier(membership), cancellationToken);
        await FinalizeAsync(job.Id, claim, cancellationToken);
    }

    // ───────────────────────────── Phase 1: staging ─────────────────────────────

    /// <summary>Streams the stored file through the reader and validator into import_rows, in bounded batches.</summary>
    private async Task StageAsync(ImportJob job, ImportClaim claim, MembershipRegistry membership, CancellationToken cancellationToken)
    {
        await DiscardPreviousStagingAsync(job.Id, claim, cancellationToken);

        var validator = new ImportRecordValidator(membership);
        var reader = readers.ForFormat(job.Format);
        var batch = new List<ImportRow>(settings.BatchSize);
        var staged = 0;

        await using (var content = await storage.OpenReadAsync(job.StorageKey, cancellationToken))
        {
            await foreach (var record in reader.ReadAsync(content, cancellationToken))
            {
                batch.Add(validator.Validate(job.Id, record));
                if (batch.Count == settings.BatchSize)
                {
                    staged = await WriteStagingBatchAsync(claim, batch, staged, cancellationToken);
                }
            }
        }

        if (batch.Count > 0)
        {
            staged = await WriteStagingBatchAsync(claim, batch, staged, cancellationToken);
        }

        await MarkStagingCompleteAsync(claim, staged, cancellationToken);
    }

    /// <summary>A previous attempt may have staged part of the file: staging always restarts from the beginning.</summary>
    private Task DiscardPreviousStagingAsync(Guid jobId, ImportClaim claim, CancellationToken cancellationToken) =>
        WithOwnedJobAsync(claim, (_, ct) => staging.ResetAsync(jobId, ct), cancellationToken);

    /// <summary>One transaction: COPY the batch and record progress (which also renews the lease).</summary>
    private async Task<int> WriteStagingBatchAsync(ImportClaim claim, List<ImportRow> batch, int stagedSoFar, CancellationToken cancellationToken)
    {
        var staged = stagedSoFar + batch.Count;
        await WithOwnedJobAsync(claim, async (owned, ct) =>
        {
            await staging.WriteBatchAsync(batch, ct);
            owned.RecordStagingProgress(staged, time.GetUtcNow(), settings.LeaseDuration);
        }, cancellationToken);
        batch.Clear();
        return staged;
    }

    private Task MarkStagingCompleteAsync(ImportClaim claim, int totalRows, CancellationToken cancellationToken) =>
        WithOwnedJobAsync(claim, (owned, _) =>
        {
            owned.CompleteStaging(totalRows);
            return Task.CompletedTask;
        }, cancellationToken);

    // ─────────────────────────── Phase 2: classification ───────────────────────────

    /// <summary>Classifies the staged rows vote by vote, one transaction per batch of votes, until none are left.</summary>
    private async Task ClassifyAsync(Guid jobId, ImportClaim claim, VoteImportClassifier classifier, CancellationToken cancellationToken)
    {
        // Invalid rows were classified at staging; on resume this also counts everything committed before the crash.
        var classified = await CountClassifiedRowsAsync(jobId, cancellationToken);

        while (true)
        {
            var batchRows = 0;
            var finished = false;
            await WithOwnedJobAsync(claim, async (owned, ct) =>
            {
                var voteIds = await GetNextUnclassifiedVoteIdsAsync(jobId, ct);
                if (voteIds.Count == 0)
                {
                    finished = true;
                    return;
                }

                batchRows = await ClassifyBatchAsync(jobId, voteIds, classifier, ct);
                owned.RecordClassificationProgress(classified + batchRows, time.GetUtcNow(), settings.LeaseDuration);
            }, cancellationToken);

            if (finished)
            {
                return;
            }

            classified += batchRows;
        }
    }

    /// <summary>Runs inside the batch transaction: lock, read current approved state, decide, persist.</summary>
    private async Task<int> ClassifyBatchAsync(Guid jobId, List<AssemblyVoteId> voteIds, VoteImportClassifier classifier, CancellationToken ct)
    {
        // Lock first, so the approved state read below cannot change before this batch commits.
        await approvedVotes.LockVotesAsync(voteIds, ct);

        var rows = await LoadUnclassifiedRowsAsync(jobId, voteIds, ct);
        var approved = await LoadApprovedDataAsync(voteIds, ct);

        var now = time.GetUtcNow(); // one timestamp for the whole batch: decisions, provenance and history agree
        var results = ClassifyVotes(jobId, rows, approved, classifier, now);

        await SaveBatchResultsAsync(jobId, rows, results, now, ct);
        return rows.Count;
    }

    /// <summary>Pure decisions: one classifier call per vote, in a stable order. No database access.</summary>
    private static List<VoteClassificationResult> ClassifyVotes(
        Guid jobId, List<ImportRow> rows, ApprovedData approved, VoteImportClassifier classifier, DateTimeOffset now) =>
        rows
            .GroupBy(r => r.AssemblyVoteId!.Value)
            .OrderBy(g => g.Key.Value, StringComparer.Ordinal)
            .Select(vote =>
            {
                var approvedVote = approved.Votes.GetValueOrDefault(vote.Key);
                var knownConflicts = approvedVote is null ? [] : approved.KnownConflicts[approvedVote.Vote.Id].ToList();
                return classifier.Classify(jobId, vote.Key, vote.ToList(), approvedVote, knownConflicts, now);
            })
            .ToList();

    /// <summary>
    /// Everything the batch decided, in the same transaction: approved-data inserts (votes, ballots, conflicts,
    /// history), the rows' classifications (write-once) and the per-vote outcomes for the report.
    /// </summary>
    private async Task SaveBatchResultsAsync(
        Guid jobId, List<ImportRow> rows, List<VoteClassificationResult> results, DateTimeOffset now, CancellationToken ct)
    {
        await approvedVotes.SaveImportResultsAsync(jobId, results, now, ct);
        await staging.SaveClassificationsAsync(jobId, rows, ct);
        db.ImportVoteOutcomes.AddRange(results.Select(r => r.Outcome));
    }

    // ───────────────────────────── Phase 3: finalize ─────────────────────────────

    /// <summary>Last write of the import: only after a check that every row has its final classification.</summary>
    private Task FinalizeAsync(Guid jobId, ImportClaim claim, CancellationToken cancellationToken) =>
        WithOwnedJobAsync(claim, async (owned, ct) =>
        {
            var unclassified = await CountUnclassifiedRowsAsync(jobId, ct);
            if (unclassified > 0)
            {
                throw new InvalidOperationException($"Import {jobId} still has {unclassified} unclassified rows; it cannot be completed.");
            }

            var hasIssues = await HasRowsNeedingAttentionAsync(jobId, ct);
            owned.RecordClassificationProgress(owned.TotalRows ?? 0, time.GetUtcNow(), settings.LeaseDuration);
            owned.Complete(hasIssues, time.GetUtcNow());
        }, cancellationToken);

    // ───────────────────────────── Queries ─────────────────────────────

    private Task<ImportJob> LoadJobAsync(Guid jobId, CancellationToken ct) =>
        db.ImportJobs.AsNoTracking().SingleAsync(j => j.Id == jobId, ct);

    /// <summary>All member states with their periods, in memory once per attempt (used for every row).</summary>
    private async Task<MembershipRegistry> LoadMembershipAsync(CancellationToken ct) =>
        new(await db.MemberStates.Include(s => s.Periods).ToListAsync(ct));

    private Task<int> CountClassifiedRowsAsync(Guid jobId, CancellationToken ct) =>
        db.ImportRows.CountAsync(r => r.ImportJobId == jobId && r.Classification != null, ct);

    private Task<int> CountUnclassifiedRowsAsync(Guid jobId, CancellationToken ct) =>
        db.ImportRows.CountAsync(r => r.ImportJobId == jobId && r.Classification == null, ct);

    /// <summary>Invalid, contradictory or conflicting rows make the import "CompletedWithIssues".</summary>
    private Task<bool> HasRowsNeedingAttentionAsync(Guid jobId, CancellationToken ct) =>
        db.ImportRows.AnyAsync(r => r.ImportJobId == jobId && IssueClassifications.Contains(r.Classification!.Value), ct);

    /// <summary>
    /// The next votes that still have unclassified rows. This is how rows of one vote are grouped although they
    /// are not contiguous in the file (ING-03), and it is the resume point after a crash.
    /// </summary>
    private Task<List<AssemblyVoteId>> GetNextUnclassifiedVoteIdsAsync(Guid jobId, CancellationToken ct) =>
        db.ImportRows
            .Where(r => r.ImportJobId == jobId && r.Classification == null && r.AssemblyVoteId != null)
            .Select(r => r.AssemblyVoteId!.Value)
            .Distinct()
            .OrderBy(id => id)
            .Take(settings.VotesPerClassificationBatch)
            .ToListAsync(ct);

    /// <summary>The file's still-unclassified rows of these votes (untracked: they are saved in bulk afterwards).</summary>
    private Task<List<ImportRow>> LoadUnclassifiedRowsAsync(Guid jobId, List<AssemblyVoteId> voteIds, CancellationToken ct)
    {
        var ids = voteIds.Select(id => (AssemblyVoteId?)id).ToList();
        return db.ImportRows.AsNoTracking()
            .Where(r => r.ImportJobId == jobId && r.Classification == null && ids.Contains(r.AssemblyVoteId))
            .ToListAsync(ct);
    }

    /// <summary>Approved votes with their ballots, and the pending/rejected conflicts used to avoid reopening (ING-12).</summary>
    private async Task<ApprovedData> LoadApprovedDataAsync(List<AssemblyVoteId> voteIds, CancellationToken ct)
    {
        var votes = await db.Votes.Where(v => voteIds.Contains(v.AssemblyVoteId)).ToListAsync(ct);
        var voteDbIds = votes.Select(v => v.Id).ToList();

        var ballots = (await db.CountryVotes.Where(cv => voteDbIds.Contains(cv.VoteId)).ToListAsync(ct))
            .ToLookup(cv => cv.VoteId);
        var knownConflicts = (await db.Conflicts
                .Where(c => voteDbIds.Contains(c.VoteId) && (c.Status == ConflictStatus.Pending || c.Status == ConflictStatus.Rejected))
                .ToListAsync(ct))
            .ToLookup(c => c.VoteId);

        var approvedVotes = votes.ToDictionary(v => v.AssemblyVoteId, v => new ApprovedVoteState(v, ballots[v.Id].ToDictionary(cv => cv.StateCode)));
        return new ApprovedData(approvedVotes, knownConflicts);
    }

    // ───────────────────────────── Ownership ─────────────────────────────

    /// <summary>
    /// One explicit transaction in which the job is loaded (tracked) and verified to still belong to this
    /// claim. <see cref="ImportJob.Attempts"/> is a concurrency token, so if another worker re-claims the job
    /// between the check and the commit, the UPDATE matches no row and nothing of this attempt is committed.
    /// </summary>
    private async Task WithOwnedJobAsync(ImportClaim claim, Func<ImportJob, CancellationToken, Task> work, CancellationToken cancellationToken)
    {
        try
        {
            await unitOfWork.ExecuteInTransactionAsync(async ct =>
            {
                var owned = await db.ImportJobs.SingleAsync(j => j.Id == claim.ImportJobId, ct);
                if (!owned.IsOwnedBy(claim.WorkerId, claim.Attempt))
                {
                    throw new ImportLeaseLostException(claim.ImportJobId);
                }

                await work(owned, ct);
            }, cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new ImportLeaseLostException(claim.ImportJobId, ex);
        }
    }
}
