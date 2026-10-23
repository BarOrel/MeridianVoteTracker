namespace MeridianVoteTracker.Domain.Common;

/// <summary>Stable error codes for rule violations the API maps to specific responses (e.g. 409).</summary>
public static class DomainErrors
{
    /// <summary>The approved value is no longer the one a conflict was detected against (stale decision).</summary>
    public const string ApprovedValueChanged = "approved_value.changed";

    /// <summary>Someone else already decided this conflict.</summary>
    public const string ConflictAlreadyResolved = "conflict.already_resolved";

    /// <summary>Position/weight was changed concurrently by another admin.</summary>
    public const string PositionChanged = "position.changed";
}
