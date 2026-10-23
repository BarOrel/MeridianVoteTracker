using MeridianVoteTracker.Domain.Common;
using MeridianVoteTracker.Domain.Conflicts;
using MeridianVoteTracker.Domain.History;
using MeridianVoteTracker.Domain.MemberStates;

namespace MeridianVoteTracker.Domain.Votes;

/// <summary>
/// The approved ballot of one state in one vote. Exactly two ways to affect it:
/// <list type="bullet">
/// <item><see cref="CreateFromImport"/>: the fact did not exist yet (classification "New", ING-10).</item>
/// <item><see cref="ApplyAcceptedConflict"/>: a human accepted a conflict (CON-04).</item>
/// </list>
/// There is intentionally no method an import could use to change an existing value (ING-08).
/// <see cref="Revision"/> is a concurrency token: two accepted conflicts on the same fact cannot both win.
/// </summary>
public sealed class CountryVote
{
    public int VoteId { get; private set; }
    public CountryCode StateCode { get; private set; }
    public Ballot Ballot { get; private set; }
    public int Revision { get; private set; }
    public ApprovalProvenance Provenance { get; private set; } = null!;

    private CountryVote() { }

    public static CountryVote CreateFromImport(int voteId, CountryCode stateCode, Ballot ballot, Guid importJobId, int rowNumber, DateTimeOffset at) =>
        new()
        {
            VoteId = voteId,
            StateCode = stateCode,
            Ballot = ballot,
            Revision = 1,
            Provenance = ApprovalProvenance.FromInitialImport(importJobId, rowNumber, at),
        };

    /// <summary>
    /// Applies a conflict that was just accepted. Verifies the approved value is still exactly the one
    /// the analyst saw (revision and value); otherwise the decision is stale and must not be applied.
    /// </summary>
    public VoteHistoryEvent ApplyAcceptedConflict(Conflict conflict, DateTimeOffset at)
    {
        if (conflict.Status != ConflictStatus.Accepted || conflict.Kind != ConflictKind.CountryBallot
            || conflict.VoteId != VoteId || conflict.StateCode != StateCode || conflict.ResolvedBy is null)
        {
            throw new InvalidOperationException($"Conflict {conflict.Id} is not an accepted ballot conflict for {VoteId}/{StateCode}.");
        }

        if (conflict.ExpectedRevision != Revision || conflict.ApprovedValueAtDetection != Ballot.ToCode())
        {
            throw new DomainException(DomainErrors.ApprovedValueChanged,
                $"The approved ballot of {StateCode} changed after the conflict was detected (now {Ballot.ToCode()}, revision {Revision}).");
        }

        if (!BallotCodes.TryParse(conflict.ProposedValue, out var proposed))
        {
            throw new InvalidOperationException($"Conflict {conflict.Id} has an invalid proposed ballot '{conflict.ProposedValue}'.");
        }

        var previous = Provenance;
        var oldValue = Ballot.ToCode();
        Ballot = proposed;
        Revision++;
        Provenance = ApprovalProvenance.FromConflictResolution(conflict, conflict.ResolvedBy, at);

        return VoteHistoryEvent.ApprovedValueCorrected(VoteId, StateCode, ConflictField.Ballot, oldValue, conflict.ProposedValue, previous, Provenance, conflict.ResolutionComment);
    }
}
