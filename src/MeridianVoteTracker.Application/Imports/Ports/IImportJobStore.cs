using MeridianVoteTracker.Domain.Imports;

namespace MeridianVoteTracker.Application.Imports.Ports;

/// <summary>
/// The database-backed work queue. Exists for one PostgreSQL-specific operation that LINQ cannot express:
/// SELECT ... FOR UPDATE SKIP LOCKED, so concurrent workers never claim the same job and never block
/// each other. The state change itself (lease, attempt counter) is done by the domain
/// (<see cref="ImportJob.BeginAttempt"/>) and committed by the caller's unit of work.
/// </summary>
public interface IImportJobStore
{
    /// <summary>
    /// Locks and returns (tracked) the oldest job that is Queued, or Processing with an expired lease
    /// (crashed worker or scheduled retry). Must run inside a transaction; the lock lasts until commit.
    /// </summary>
    Task<ImportJob?> LockNextClaimableAsync(DateTimeOffset now, CancellationToken cancellationToken);
}
