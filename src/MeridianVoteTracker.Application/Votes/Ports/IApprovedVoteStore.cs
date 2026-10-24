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

    /// <summary>
    /// CON-04/05: decides a conflict under the vote's lock, reading the current state inside the transaction.
    /// Accept changes the approved value (revision-checked), supersedes other pending proposals for the same
    /// fact and writes the history; Reject only records the decision. Returns false if the conflict does not exist.
    /// </summary>
    /// <exception cref="Domain.Common.DomainException">Already resolved, or the approved value changed since detection.</exception>
    Task<bool> ResolveConflictAsync(Guid conflictId, bool accept, string actor, string? comment, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>POS-01/03: sets position and weight with optimistic concurrency and a history event. False if the vote does not exist.</summary>
    /// <exception cref="Domain.Common.DomainException">The position was changed by someone else since <paramref name="expectedRevision"/>.</exception>
    Task<bool> SetPositionAsync(int voteId, Position position, Weight weight, int expectedRevision, string actor, DateTimeOffset at, CancellationToken cancellationToken);
}
