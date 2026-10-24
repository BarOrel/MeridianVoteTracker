namespace MeridianVoteTracker.Application.Snapshots.Ports;

/// <summary>
/// The SQL-specific part of snapshots: claiming pending work (FOR UPDATE SKIP LOCKED) and computing the
/// results set-based over up to ~1M ballots in one statement (one consistent view of approved data).
/// Joins the caller's IUnitOfWork transaction.
/// </summary>
public interface ISnapshotCalculator
{
    /// <summary>Locks the oldest pending snapshot until commit, or returns null.</summary>
    Task<Guid?> LockNextPendingAsync(CancellationToken cancellationToken);

    /// <summary>Copies the selected votes' positions/weights and approved ballots and stores per-state results.</summary>
    Task CalculateAsync(Guid snapshotId, IReadOnlyList<int> voteIds, CancellationToken cancellationToken);
}
