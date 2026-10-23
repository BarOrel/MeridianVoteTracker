using System.Globalization;
using MeridianVoteTracker.Domain.Common;
using MeridianVoteTracker.Domain.Conflicts;
using MeridianVoteTracker.Domain.History;

namespace MeridianVoteTracker.Domain.Votes;

/// <summary>
/// One Assembly vote with its approved metadata and the Institute's position.
/// Metadata (session, date, title) follows the same rule as ballots: created once by an import,
/// changed only through an accepted conflict (CON-03). Position/weight are set by an Admin (POS-01).
/// Two independent revision counters keep metadata corrections and position edits from
/// producing false concurrency conflicts with each other.
/// </summary>
public sealed class Vote
{
    public const string DateFormat = "yyyy-MM-dd";

    public int Id { get; private set; }
    public AssemblyVoteId AssemblyVoteId { get; private set; }
    public int Session { get; private set; }
    public DateOnly VoteDate { get; private set; }
    public string Title { get; private set; } = null!;
    public int MetadataRevision { get; private set; }
    public ApprovalProvenance MetadataProvenance { get; private set; } = null!;

    public Position? Position { get; private set; }
    public Weight? Weight { get; private set; }
    public int PositionRevision { get; private set; }

    private Vote() { }

    public static Vote CreateFromImport(AssemblyVoteId assemblyVoteId, int session, DateOnly voteDate, string title, Guid importJobId, int firstRowNumber, DateTimeOffset at)
    {
        if (session <= 0)
        {
            throw new DomainException("vote.invalid_session", $"Session must be positive; got {session}.");
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            throw new DomainException("vote.title_required", "Vote title is required.");
        }

        return new Vote
        {
            AssemblyVoteId = assemblyVoteId,
            Session = session,
            VoteDate = voteDate,
            Title = title,
            MetadataRevision = 1,
            MetadataProvenance = ApprovalProvenance.FromInitialImport(importJobId, firstRowNumber, at),
        };
    }

    /// <summary>Canonical text of a metadata field, the same representation the export file uses.</summary>
    public string GetMetadataValue(ConflictField field) => field switch
    {
        ConflictField.Session => FormatSession(Session),
        ConflictField.VoteDate => FormatDate(VoteDate),
        ConflictField.Title => Title,
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Not a vote metadata field."),
    };

    public VoteHistoryEvent ApplyAcceptedConflict(Conflict conflict, DateTimeOffset at)
    {
        if (conflict.Status != ConflictStatus.Accepted || conflict.Kind != ConflictKind.VoteMetadata
            || conflict.VoteId != Id || conflict.ResolvedBy is null)
        {
            throw new InvalidOperationException($"Conflict {conflict.Id} is not an accepted metadata conflict for vote {Id}.");
        }

        var oldValue = GetMetadataValue(conflict.Field);
        if (conflict.ExpectedRevision != MetadataRevision || conflict.ApprovedValueAtDetection != oldValue)
        {
            throw new DomainException(DomainErrors.ApprovedValueChanged,
                $"The approved {conflict.Field} of {AssemblyVoteId} changed after the conflict was detected.");
        }

        switch (conflict.Field)
        {
            case ConflictField.Session:
                Session = int.Parse(conflict.ProposedValue, CultureInfo.InvariantCulture);
                break;
            case ConflictField.VoteDate:
                VoteDate = DateOnly.ParseExact(conflict.ProposedValue, DateFormat, CultureInfo.InvariantCulture);
                break;
            case ConflictField.Title:
                Title = conflict.ProposedValue;
                break;
        }

        var previous = MetadataProvenance;
        MetadataRevision++;
        MetadataProvenance = ApprovalProvenance.FromConflictResolution(conflict, conflict.ResolvedBy, at);
        return VoteHistoryEvent.ApprovedValueCorrected(Id, null, conflict.Field, oldValue, conflict.ProposedValue, previous, MetadataProvenance, conflict.ResolutionComment);
    }

    /// <summary>Admin sets the desired outcome and weight. Optimistic concurrency via <paramref name="expectedRevision"/>.</summary>
    public VoteHistoryEvent SetPosition(Position position, Weight weight, int expectedRevision, string actor, DateTimeOffset at)
    {
        if (expectedRevision != PositionRevision)
        {
            throw new DomainException(DomainErrors.PositionChanged, $"The position of {AssemblyVoteId} was changed by someone else.");
        }

        var oldValue = Position is null ? null : FormatPosition(Position.Value, Weight!.Value);
        Position = position;
        Weight = weight;
        PositionRevision++;
        return VoteHistoryEvent.PositionChanged(Id, oldValue, FormatPosition(position, weight), actor, at);
    }

    public static string FormatDate(DateOnly date) => date.ToString(DateFormat, CultureInfo.InvariantCulture);

    public static string FormatSession(int session) => session.ToString(CultureInfo.InvariantCulture);

    private static string FormatPosition(Position position, Weight weight) => $"{position}, weight {weight}";
}
