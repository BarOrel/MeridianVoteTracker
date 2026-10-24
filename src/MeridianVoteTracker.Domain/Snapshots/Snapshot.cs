using MeridianVoteTracker.Domain.Common;

namespace MeridianVoteTracker.Domain.Snapshots;

public enum SnapshotStatus
{
    Pending,
    Completed,
    Failed,
}

/// <summary>
/// A saved calculation of the Alignment Index for a set of votes (SNP-01..04). Created Pending; a background
/// worker copies the inputs (positions, weights, ballots) into snapshot tables and computes the results in one
/// statement, then completes it. A completed snapshot is immutable (database triggers); later corrections,
/// decisions or weight changes never affect it. For an updated calculation, create a new snapshot.
/// </summary>
public sealed class Snapshot
{
    public const int MaxNameLength = 128;

    /// <summary>Scoring rules applied by every snapshot; shown with each result (assignment §7.6, open rules decided here).</summary>
    public static readonly IReadOnlyList<string> Rules =
    [
        "Score = 100 × (sum of weights where the ballot matched the Institute's position) ÷ (sum of weights of counted votes).",
        "Only YES and NO ballots are counted. ABSTAIN and ABSENT are not counted (neither numerator nor denominator).",
        "Votes held while a state was not a member have no ballot for it and are not counted.",
        "A state with no counted vote in the snapshot has no score (n/a) and is not ranked.",
        "Ranking: higher score first; equal scores share a rank (1, 2, 2, 4). Scores are rounded to one decimal.",
        "Every selected vote must have a position and weight; a vote without one blocks creation (POS-04).",
    ];

    private List<int> _voteIds = [];

    public Guid Id { get; private set; }
    public string Name { get; private set; } = null!;
    public string Selection { get; private set; } = null!;
    public string CreatedBy { get; private set; } = null!;
    public DateTimeOffset CreatedAt { get; private set; }
    public SnapshotStatus Status { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public string? Error { get; private set; }
    public IReadOnlyList<int> VoteIds => _voteIds;

    private Snapshot() { }

    public static Snapshot Create(string name, string selection, IReadOnlyCollection<int> voteIds, string createdBy, DateTimeOffset at)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > MaxNameLength)
        {
            throw new DomainException("snapshot.name_invalid", $"A name of 1 to {MaxNameLength} characters is required.");
        }

        if (voteIds.Count == 0)
        {
            throw new DomainException("snapshot.no_votes", "A snapshot needs at least one vote.");
        }

        return new Snapshot
        {
            Id = Guid.NewGuid(),
            Name = name.Trim(),
            Selection = selection,
            _voteIds = voteIds.Distinct().Order().ToList(),
            CreatedBy = createdBy,
            CreatedAt = at,
            Status = SnapshotStatus.Pending,
        };
    }

    public void Complete(DateTimeOffset at)
    {
        EnsurePending();
        Status = SnapshotStatus.Completed;
        CompletedAt = at;
    }

    public void Fail(string error, DateTimeOffset at)
    {
        EnsurePending();
        Status = SnapshotStatus.Failed;
        Error = error.Length > 2000 ? error[..2000] : error;
        CompletedAt = at;
    }

    private void EnsurePending()
    {
        if (Status != SnapshotStatus.Pending)
        {
            throw new DomainException("snapshot.immutable", $"Snapshot {Id} is {Status} and can no longer change.");
        }
    }
}

/// <summary>SNP-05: the one snapshot an admin published for a quarter ("2024-Q1"). Insert-only.</summary>
public sealed class SnapshotPublication
{
    public string Quarter { get; private set; } = null!;
    public Guid SnapshotId { get; private set; }
    public string PublishedBy { get; private set; } = null!;
    public DateTimeOffset PublishedAt { get; private set; }

    private SnapshotPublication() { }

    public SnapshotPublication(string quarter, Guid snapshotId, string publishedBy, DateTimeOffset at)
    {
        Quarter = quarter;
        SnapshotId = snapshotId;
        PublishedBy = publishedBy;
        PublishedAt = at;
    }
}
