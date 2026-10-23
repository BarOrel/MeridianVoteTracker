using MeridianVoteTracker.Domain.Conflicts;
using MeridianVoteTracker.Domain.MemberStates;
using MeridianVoteTracker.Domain.Votes;

namespace MeridianVoteTracker.Domain.History;

public enum VoteHistoryEventType
{
    VoteCreated,
    CountryVotesAdded,
    ConflictOpened,
    ConflictRejected,
    ConflictSuperseded,
    ApprovedValueCorrected,
    PositionChanged,
}

/// <summary>
/// Append-only timeline entry of a vote (HIS-02..04). Every change to approved data records the
/// previous and new value with the provenance (import job + row) of both, the actor, the time and,
/// for decisions, the conflict and comment. Instances have no mutators; the table rejects
/// UPDATE/DELETE/TRUNCATE via triggers, so the timeline cannot be rewritten after the fact.
/// </summary>
public sealed class VoteHistoryEvent
{
    public long Id { get; private set; }
    public int VoteId { get; private set; }
    public CountryCode? StateCode { get; private set; }
    public VoteHistoryEventType Type { get; private set; }
    public ConflictField? Field { get; private set; }
    public string? OldValue { get; private set; }
    public string? NewValue { get; private set; }
    public Guid? OldSourceImportJobId { get; private set; }
    public int? OldSourceRowNumber { get; private set; }
    public Guid? NewSourceImportJobId { get; private set; }
    public int? NewSourceRowNumber { get; private set; }
    public Guid? ConflictId { get; private set; }
    public string Actor { get; private set; } = null!;
    public DateTimeOffset OccurredAt { get; private set; }
    public string? Comment { get; private set; }

    private VoteHistoryEvent() { }

    public static VoteHistoryEvent VoteCreated(int voteId, Guid importJobId, int firstRowNumber, DateTimeOffset at) => new()
    {
        VoteId = voteId,
        Type = VoteHistoryEventType.VoteCreated,
        NewSourceImportJobId = importJobId,
        NewSourceRowNumber = firstRowNumber,
        Actor = ApprovalProvenance.ImportActor,
        OccurredAt = at,
    };

    /// <summary>One summary event per vote and import; per-row provenance lives on each CountryVote.</summary>
    public static VoteHistoryEvent CountryVotesAdded(int voteId, Guid importJobId, int count, DateTimeOffset at) => new()
    {
        VoteId = voteId,
        Type = VoteHistoryEventType.CountryVotesAdded,
        NewValue = count.ToString(System.Globalization.CultureInfo.InvariantCulture),
        NewSourceImportJobId = importJobId,
        Actor = ApprovalProvenance.ImportActor,
        OccurredAt = at,
    };

    public static VoteHistoryEvent ConflictOpened(Conflict conflict) => FromConflict(conflict, VoteHistoryEventType.ConflictOpened, ApprovalProvenance.ImportActor, conflict.DetectedAt);

    public static VoteHistoryEvent ConflictRejected(Conflict conflict) => FromResolvedConflict(conflict, VoteHistoryEventType.ConflictRejected);

    public static VoteHistoryEvent ConflictSuperseded(Conflict conflict) => FromResolvedConflict(conflict, VoteHistoryEventType.ConflictSuperseded);

    public static VoteHistoryEvent ApprovedValueCorrected(
        int voteId, CountryCode? stateCode, ConflictField field, string oldValue, string newValue,
        ApprovalProvenance previous, ApprovalProvenance current, string? comment) => new()
    {
        VoteId = voteId,
        StateCode = stateCode,
        Type = VoteHistoryEventType.ApprovedValueCorrected,
        Field = field,
        OldValue = oldValue,
        NewValue = newValue,
        OldSourceImportJobId = previous.SourceImportJobId,
        OldSourceRowNumber = previous.SourceRowNumber,
        NewSourceImportJobId = current.SourceImportJobId,
        NewSourceRowNumber = current.SourceRowNumber,
        ConflictId = current.ConflictId,
        Actor = current.ApprovedBy,
        OccurredAt = current.ApprovedAt,
        Comment = comment,
    };

    public static VoteHistoryEvent PositionChanged(int voteId, string? oldValue, string newValue, string actor, DateTimeOffset at) => new()
    {
        VoteId = voteId,
        Type = VoteHistoryEventType.PositionChanged,
        OldValue = oldValue,
        NewValue = newValue,
        Actor = actor,
        OccurredAt = at,
    };

    private static VoteHistoryEvent FromResolvedConflict(Conflict conflict, VoteHistoryEventType type) =>
        FromConflict(conflict, type, conflict.ResolvedBy ?? throw new InvalidOperationException($"Conflict {conflict.Id} is not resolved."), conflict.ResolvedAt!.Value);

    private static VoteHistoryEvent FromConflict(Conflict conflict, VoteHistoryEventType type, string actor, DateTimeOffset at) => new()
    {
        VoteId = conflict.VoteId,
        StateCode = conflict.StateCode,
        Type = type,
        Field = conflict.Field,
        OldValue = conflict.ApprovedValueAtDetection,
        NewValue = conflict.ProposedValue,
        NewSourceImportJobId = conflict.SourceImportJobId,
        NewSourceRowNumber = conflict.SourceRowNumber,
        ConflictId = conflict.Id,
        Actor = actor,
        OccurredAt = at,
        Comment = conflict.ResolutionComment,
    };
}
