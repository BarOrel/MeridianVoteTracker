using MeridianVoteTracker.Application.Abstractions;
using MeridianVoteTracker.Application.Imports.Pipeline;
using MeridianVoteTracker.Application.Imports.Ports;
using MeridianVoteTracker.Domain.Imports;
using MeridianVoteTracker.Domain.MemberStates;
using Microsoft.EntityFrameworkCore;

namespace MeridianVoteTracker.Application.Imports.ProcessImport;

/// <summary>
/// Runs one claimed import attempt. Steps are separate and format-independent after the reader:
/// <code>
/// storage → reader (parse) → validator (row rules, normalization) → staging (COPY, bounded batches)
///         → [Phase 3: classification against approved data] → finalize
/// </code>
/// Transactions: one per staging batch (rows + progress + lease heartbeat commit together), one to
/// reset staging, one to close staging. Memory is bounded by the batch size: the file is streamed and
/// rows are never tracked. Every write verifies the claim (owner + attempt) is still current.
/// </summary>
public sealed class ProcessImportUseCase(
    IApplicationDbContext db,
    IUnitOfWork unitOfWork,
    IImportFileStorage storage,
    ImportFileReaderResolver readers,
    IImportStagingStore staging,
    TimeProvider time,
    ImportProcessingSettings settings)
{
    public async Task ExecuteAsync(ImportClaim claim, CancellationToken cancellationToken)
    {
        var job = await db.ImportJobs.AsNoTracking().SingleAsync(j => j.Id == claim.ImportJobId, cancellationToken);

        if (job.Phase == ImportPhase.Staging)
        {
            await StageAsync(job, claim, cancellationToken);
        }

        // Phase 3 inserts classification here (resumable per vote batch). Until then the staged job is parked.
        await WithOwnedJobAsync(claim, (owned, _) =>
        {
            owned.ParkAwaitingClassification();
            return Task.CompletedTask;
        }, cancellationToken);
    }

    private async Task StageAsync(ImportJob job, ImportClaim claim, CancellationToken cancellationToken)
    {
        // A previous attempt may have staged part of the file: staging always restarts from the beginning.
        await WithOwnedJobAsync(claim, (_, ct) => staging.ResetAsync(job.Id, ct), cancellationToken);

        var validator = new ImportRecordValidator(await LoadMembershipAsync(cancellationToken));
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
