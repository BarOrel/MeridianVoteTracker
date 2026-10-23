using MeridianVoteTracker.Application.Abstractions;
using MeridianVoteTracker.Application.Imports.Ports;

namespace MeridianVoteTracker.Application.Imports.ProcessImport;

/// <summary>
/// Takes ownership of the next import (new, or abandoned by a crashed worker, or due for retry).
/// Transaction: one, holding the row lock from <see cref="IImportJobStore.LockNextClaimableAsync"/> until
/// the lease is committed, so two workers can never claim the same job.
/// A job that has used all attempts is failed instead of claimed.
/// </summary>
public sealed class ClaimNextImportUseCase(IImportJobStore jobs, IUnitOfWork unitOfWork, TimeProvider time, ImportProcessingSettings settings)
{
    public Task<ImportClaim?> ExecuteAsync(string workerId, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var now = time.GetUtcNow();
            var job = await jobs.LockNextClaimableAsync(now, ct);
            if (job is null)
            {
                return null;
            }

            if (!job.HasAttemptsLeft)
            {
                job.Fail($"Gave up after {job.Attempts} attempts. Last error: {job.Error ?? "worker stopped responding"}", now);
                return null;
            }

            job.BeginAttempt(workerId, now, settings.LeaseDuration);
            return new ImportClaim(job.Id, workerId, job.Attempts, job.CorrelationId);
        }, cancellationToken);
}
