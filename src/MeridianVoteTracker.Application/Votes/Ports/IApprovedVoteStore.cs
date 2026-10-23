using MeridianVoteTracker.Domain.Imports;
using MeridianVoteTracker.Domain.Votes;

namespace MeridianVoteTracker.Application.Votes.Ports;

/// <summary>
/// The only writer of approved data, conflicts and the vote history (restricted entity types).
/// Exists because these writes must be atomic with their audit events, bulk-capable, and serialized
/// per vote; ordinary use cases can only read this data through IApplicationDbContext.
/// All methods join the caller's IUnitOfWork transaction.
/// </summary>
public interface IApprovedVoteStore
{
    /// <summary>
    /// Serializes work on these votes until the transaction ends (transaction-scoped advisory locks, taken in a
    /// fixed order). Two imports classifying the same vote run one after the other, so the second sees the
    /// first one's result instead of racing it.
    /// </summary>
    Task LockVotesAsync(IReadOnlyCollection<AssemblyVoteId> voteIds, CancellationToken cancellationToken);

    /// <summary>
    /// Persists what a classification batch decided: new votes, new country ballots (initial-import provenance)
    /// and new conflicts, each with its history events. Insert-only: an import can never change an existing
    /// approved value. Links each new vote's id into its <see cref="ImportVoteOutcome"/>.
    /// </summary>
    Task SaveImportResultsAsync(Guid importJobId, IReadOnlyList<VoteClassificationResult> results, DateTimeOffset at, CancellationToken cancellationToken);
}
