using MeridianVoteTracker.Domain.Votes;

namespace MeridianVoteTracker.Domain.Snapshots;

// Frozen copies written only by the snapshot calculation (one SQL statement) and never changed afterwards
// (triggers). They are what makes a published index reproducible: they do not reference mutable approved data.

/// <summary>A selected vote as it was when the snapshot was calculated.</summary>
public sealed class SnapshotVote
{
    public Guid SnapshotId { get; private set; }
    public int VoteId { get; private set; }
    public string AssemblyVoteId { get; private set; } = null!;
    public string Title { get; private set; } = null!;
    public DateOnly VoteDate { get; private set; }
    public Position Position { get; private set; }
    public short Weight { get; private set; }
}

/// <summary>One state's approved ballot in one vote at calculation time, and its contribution to the score.</summary>
public sealed class SnapshotEntry
{
    public Guid SnapshotId { get; private set; }
    public int VoteId { get; private set; }
    public string StateCode { get; private set; } = null!;
    public string Ballot { get; private set; } = null!;
    public bool Counted { get; private set; }
    public bool Matching { get; private set; }
    public short Weight { get; private set; }
}

/// <summary>Score and rank of one member state. Score is null (n/a) when no vote was counted for it.</summary>
public sealed class SnapshotResult
{
    public Guid SnapshotId { get; private set; }
    public string StateCode { get; private set; } = null!;
    public int CountedWeight { get; private set; }
    public int MatchingWeight { get; private set; }
    public decimal? Score { get; private set; }
    public int? Rank { get; private set; }
}
