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

    public async Task ExecuteAsync(ImportClaim claim, CancellationToken cancellationToken)
    {
        var job = await db.ImportJobs.AsNoTracking().SingleAsync(j => j.Id == claim.ImportJobId, cancellationToken);
        var membership = await LoadMembershipAsync(cancellationToken);

        if (job.Phase == ImportPhase.Staging)
        {
            await StageAsync(job, claim, membership, cancellationToken);
        }

        await ClassifyAsync(job.Id, claim, new VoteImportClassifier(membership), cancellationToken);
        await FinalizeAsync(job.Id, claim, cancellationToken);
    }

    private async Task StageAsync(ImportJob job, ImportClaim claim, MembershipRegistry membership, CancellationToken cancellationToken)
    {
        // A previous attempt may have staged part of the file: staging always restarts from the beginning.
        await WithOwnedJobAsync(claim, (_, ct) => staging.ResetAsync(job.Id, ct), cancellationToken);

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
                    staged = await WriteBatchAsync(claim, batch, staged, cancellationToken);
                }
            }
        }

        if (batch.Count > 0)
        {
            staged = await WriteBatchAsync(claim, batch, staged, cancellationToken);
        }

        await WithOwnedJobAsync(claim, (owned, _) =>
        {
            owned.CompleteStaging(staged);
            return Task.CompletedTask;
        }, cancellationToken);
    }

    private async Task<int> WriteBatchAsync(ImportClaim claim, List<ImportRow> batch, int stagedSoFar, CancellationToken cancellationToken)
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

    private async Task ClassifyAsync(Guid jobId, ImportClaim claim, VoteImportClassifier classifier, CancellationToken cancellationToken)
    {
        // Invalid rows were classified at staging; on resume this also counts everything committed before the crash.
        var classified = await db.ImportRows.CountAsync(r => r.ImportJobId == jobId && r.Classification != null, cancellationToken);

        while (true)
        {
            var batchRows = 0;
            var finished = false;
            await WithOwnedJobAsync(claim, async (owned, ct) =>
            {
                var voteIds = await db.ImportRows
                    .Where(r => r.ImportJobId == jobId && r.Classification == null && r.AssemblyVoteId != null)
                    .Select(r => r.AssemblyVoteId!.Value)
                    .Distinct()
                    .OrderBy(id => id)
                    .Take(settings.VotesPerClassificationBatch)
                    .ToListAsync(ct);
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
        await approvedVotes.LockVotesAsync(voteIds, ct);

        var nullableIds = voteIds.Select(id => (AssemblyVoteId?)id).ToList();
        var rows = await db.ImportRows.AsNoTracking()
            .Where(r => r.ImportJobId == jobId && r.Classification == null && nullableIds.Contains(r.AssemblyVoteId))
            .ToListAsync(ct);

        var votes = await db.Votes.Where(v => voteIds.Contains(v.AssemblyVoteId)).ToListAsync(ct);
        var existingIds = votes.Select(v => v.Id).ToList();
        var countryVotes = (await db.CountryVotes.Where(cv => existingIds.Contains(cv.VoteId)).ToListAsync(ct)).ToLookup(cv => cv.VoteId);
        var conflicts = (await db.Conflicts
                .Where(c => existingIds.Contains(c.VoteId) && (c.Status == ConflictStatus.Pending || c.Status == ConflictStatus.Rejected))
                .ToListAsync(ct))
            .ToLookup(c => c.VoteId);
        var approvedById = votes.ToDictionary(v => v.AssemblyVoteId,
            v => new ApprovedVoteState(v, countryVotes[v.Id].ToDictionary(cv => cv.StateCode)));

        var now = time.GetUtcNow();
        var results = rows
            .GroupBy(r => r.AssemblyVoteId!.Value)
            .OrderBy(g => g.Key.Value, StringComparer.Ordinal)
            .Select(g =>
            {
                var approved = approvedById.GetValueOrDefault(g.Key);
                return classifier.Classify(jobId, g.Key, g.ToList(), approved,
                    approved is null ? [] : conflicts[approved.Vote.Id].ToList(), now);
            })
            .ToList();

        await approvedVotes.SaveImportResultsAsync(jobId, results, now, ct);
        await staging.SaveClassificationsAsync(jobId, rows, ct);
        db.ImportVoteOutcomes.AddRange(results.Select(r => r.Outcome));
        return rows.Count;
    }

    /// <summary>Last write of the import: only after a check that every row has its final classification.</summary>
    private async Task FinalizeAsync(Guid jobId, ImportClaim claim, CancellationToken cancellationToken) =>
        await WithOwnedJobAsync(claim, async (owned, ct) =>
        {
            var unclassified = await db.ImportRows.CountAsync(r => r.ImportJobId == jobId && r.Classification == null, ct);
            if (unclassified > 0)
            {
                throw new InvalidOperationException($"Import {jobId} still has {unclassified} unclassified rows; it cannot be completed.");
            }

            var hasIssues = await db.ImportRows.AnyAsync(
                r => r.ImportJobId == jobId && IssueClassifications.Contains(r.Classification!.Value), ct);
            owned.RecordClassificationProgress(owned.TotalRows ?? 0, time.GetUtcNow(), settings.LeaseDuration);
            owned.Complete(hasIssues, time.GetUtcNow());
        }, cancellationToken);

    private async Task<MembershipRegistry> LoadMembershipAsync(CancellationToken cancellationToken) =>
        new(await db.MemberStates.Include(s => s.Periods).ToListAsync(cancellationToken));

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
