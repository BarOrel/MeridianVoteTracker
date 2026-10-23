using MeridianVoteTracker.Domain.Common;
using MeridianVoteTracker.Domain.MemberStates;
using MeridianVoteTracker.Domain.Votes;

namespace MeridianVoteTracker.Domain.Conflicts;

public enum ConflictKind
{
    CountryBallot,
    VoteMetadata,
}

public enum ConflictField
{
    Ballot,
    Session,
    VoteDate,
    Title,
}

public enum ConflictStatus
{
    Pending,
    Accepted,
    Rejected,

    /// <summary>Another conflict on the same fact was accepted first; this proposal targeted a value that no longer exists.</summary>
    Superseded,
}

/// <summary>
/// A newer file disagrees with an approved value; a human decides (ING-06, CON-*).
/// Captures the approved value and its revision at detection time, so a decision is applied only
/// if the analyst saw the current truth. Values are stored as canonical file text (ballot code,
/// ISO date, session number, title) so both kinds share one shape.
/// Invariant: a conflict leaves Pending exactly once and is immutable afterwards.
/// </summary>
public sealed class Conflict
{
    public const string ResolutionActor = "system:resolution";

    public Guid Id { get; private set; }
    public ConflictKind Kind { get; private set; }
    public int VoteId { get; private set; }
    public CountryCode? StateCode { get; private set; }
    public ConflictField Field { get; private set; }
    public string ApprovedValueAtDetection { get; private set; } = null!;
    public int ExpectedRevision { get; private set; }
    public string ProposedValue { get; private set; } = null!;
    public Guid SourceImportJobId { get; private set; }
    public int? SourceRowNumber { get; private set; }
    public DateTimeOffset DetectedAt { get; private set; }

    public ConflictStatus Status { get; private set; }
    public string? ResolvedBy { get; private set; }
    public DateTimeOffset? ResolvedAt { get; private set; }
    public string? ResolutionComment { get; private set; }
    public Guid? SupersededByConflictId { get; private set; }

    private Conflict() { }

    public static Conflict ForCountryBallot(CountryVote approved, Ballot proposed, Guid sourceImportJobId, int sourceRowNumber, DateTimeOffset detectedAt)
    {
        if (approved.Ballot == proposed)
        {
            throw new DomainException("conflict.no_difference", "A conflict requires a proposed value different from the approved one.");
        }

        return new Conflict
        {
            Id = Guid.NewGuid(),
            Kind = ConflictKind.CountryBallot,
            VoteId = approved.VoteId,
            StateCode = approved.StateCode,
            Field = ConflictField.Ballot,
            ApprovedValueAtDetection = approved.Ballot.ToCode(),
            ExpectedRevision = approved.Revision,
            ProposedValue = proposed.ToCode(),
            SourceImportJobId = sourceImportJobId,
            SourceRowNumber = sourceRowNumber,
            DetectedAt = detectedAt,
            Status = ConflictStatus.Pending,
        };
    }

    public static Conflict ForVoteMetadata(Vote approved, ConflictField field, string proposedValue, Guid sourceImportJobId, int? sourceRowNumber, DateTimeOffset detectedAt)
    {
        if (field == ConflictField.Ballot)
        {
            throw new ArgumentOutOfRangeException(nameof(field), field, "Ballot is not a vote metadata field.");
        }

        var approvedValue = approved.GetMetadataValue(field);
        if (approvedValue == proposedValue)
        {
            throw new DomainException("conflict.no_difference", "A conflict requires a proposed value different from the approved one.");
        }

        return new Conflict
        {
            Id = Guid.NewGuid(),
            Kind = ConflictKind.VoteMetadata,
            VoteId = approved.Id,
            Field = field,
            ApprovedValueAtDetection = approvedValue,
            ExpectedRevision = approved.MetadataRevision,
            ProposedValue = proposedValue,
            SourceImportJobId = sourceImportJobId,
            SourceRowNumber = sourceRowNumber,
            DetectedAt = detectedAt,
            Status = ConflictStatus.Pending,
        };
    }

    public void Accept(string actor, DateTimeOffset at, string? comment) => Resolve(ConflictStatus.Accepted, actor, at, comment);

    public void Reject(string actor, DateTimeOffset at, string? comment) => Resolve(ConflictStatus.Rejected, actor, at, comment);

    public void Supersede(Guid acceptedConflictId, DateTimeOffset at)
    {
        Resolve(ConflictStatus.Superseded, ResolutionActor, at, null);
        SupersededByConflictId = acceptedConflictId;
    }

    private void Resolve(ConflictStatus outcome, string actor, DateTimeOffset at, string? comment)
    {
        if (Status != ConflictStatus.Pending)
        {
            throw new DomainException(DomainErrors.ConflictAlreadyResolved,
                $"Conflict {Id} was already resolved ({Status}) by {ResolvedBy} at {ResolvedAt:O}.");
        }

        Status = outcome;
        ResolvedBy = actor;
        ResolvedAt = at;
        ResolutionComment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
    }
}
