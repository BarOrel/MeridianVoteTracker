using MeridianVoteTracker.Domain.Conflicts;

namespace MeridianVoteTracker.Domain.Votes;

public enum ApprovalOrigin
{
    /// <summary>First value ever stored for this fact, taken from an import (glossary: "data that was imported").</summary>
    InitialImport,

    /// <summary>A human accepted a conflict; the value came from the conflict's source file.</summary>
    ConflictResolution,
}

/// <summary>
/// Where the current approved value came from and who approved it (HIS-02). Stored on the approved
/// row itself, so the provenance of the initial ~1M imported values costs no extra rows; every later
/// change also writes a <see cref="History.VoteHistoryEvent"/> carrying both old and new provenance.
/// </summary>
public sealed record ApprovalProvenance(
    ApprovalOrigin Origin,
    Guid SourceImportJobId,
    int? SourceRowNumber,
    Guid? ConflictId,
    string ApprovedBy,
    DateTimeOffset ApprovedAt)
{
    public const string ImportActor = "system:import";

    public static ApprovalProvenance FromInitialImport(Guid importJobId, int? rowNumber, DateTimeOffset at) =>
        new(ApprovalOrigin.InitialImport, importJobId, rowNumber, null, ImportActor, at);

    public static ApprovalProvenance FromConflictResolution(Conflict conflict, string actor, DateTimeOffset at) =>
        new(ApprovalOrigin.ConflictResolution, conflict.SourceImportJobId, conflict.SourceRowNumber, conflict.Id, actor, at);
}
