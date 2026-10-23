using MeridianVoteTracker.Application.Abstractions;
using MeridianVoteTracker.Application.Imports.Pipeline;
using Microsoft.EntityFrameworkCore;

namespace MeridianVoteTracker.Application.Imports.ProcessImport;

/// <summary>
/// Decides what a failed attempt means. A file-level problem (<see cref="ImportFileException"/>) fails the
/// job at once: retrying would fail the same way. Anything else is treated as transient and retried
/// after a growing delay, until <see cref="Domain.Imports.ImportJob.MaxAttempts"/>; the error is stored
/// so the status endpoint shows it. Transaction: one. Ignored if this worker no longer owns the job.
/// </summary>
public sealed class RecordImportFailureUseCase(IApplicationDbContext db, IUnitOfWork unitOfWork, TimeProvider time, ImportProcessingSettings settings)
{
    public Task ExecuteAsync(ImportClaim claim, Exception failure, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var job = await db.ImportJobs.SingleAsync(j => j.Id == claim.ImportJobId, ct);
            if (!job.IsOwnedBy(claim.WorkerId, claim.Attempt))
            {
                return;
            }

            var now = time.GetUtcNow();
            if (failure is ImportFileException fileProblem)
            {
                job.Fail($"{fileProblem.Code}: {fileProblem.Message}", now);
            }
            else if (job.HasAttemptsLeft)
            {
                job.RecordFailedAttempt($"Attempt {job.Attempts} failed: {failure.Message}", now + settings.RetryDelay * job.Attempts);
            }
            else
            {
                job.Fail($"Failed after {job.Attempts} attempts: {failure.Message}", now);
            }
        }, cancellationToken);
}
