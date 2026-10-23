using MeridianVoteTracker.Domain.Common;
using MeridianVoteTracker.Domain.Conflicts;
using MeridianVoteTracker.Domain.History;
using MeridianVoteTracker.Domain.MemberStates;
using MeridianVoteTracker.Domain.Votes;

namespace MeridianVoteTracker.UnitTests.Domain;

/// <summary>Initial import vs. human-approved correction, conflicts and concurrency at the domain level.</summary>
public class ApprovedDataTests
{
    private static readonly DateTimeOffset T0 = new(2024, 4, 22, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid OriginalFile = Guid.NewGuid();
    private static readonly Guid CorrectedFile = Guid.NewGuid();
    private static readonly CountryCode Bra = CountryCode.Create("BRA");

    private static CountryVote BrazilVotedYes() => CountryVote.CreateFromImport(101, Bra, Ballot.Yes, OriginalFile, 12, T0);

    [Fact]
    public void Initial_import_creates_revision_1_with_import_provenance()
    {
        var cv = BrazilVotedYes();

        Assert.Equal(1, cv.Revision);
        Assert.Equal(ApprovalOrigin.InitialImport, cv.Provenance.Origin);
        Assert.Equal(OriginalFile, cv.Provenance.SourceImportJobId);
        Assert.Equal(12, cv.Provenance.SourceRowNumber);
        Assert.Equal(ApprovalProvenance.ImportActor, cv.Provenance.ApprovedBy);
    }

    [Fact]
    public void Accepted_conflict_changes_value_and_records_full_audit_trail()
    {
        var cv = BrazilVotedYes();
        var conflict = Conflict.ForCountryBallot(cv, Ballot.Abstain, CorrectedFile, 7, T0.AddMinutes(5));
        conflict.Accept("dana", T0.AddMinutes(10), "Corrigendum of 18 April");

        var evt = cv.ApplyAcceptedConflict(conflict, T0.AddMinutes(10));

        Assert.Equal(Ballot.Abstain, cv.Ballot);
        Assert.Equal(2, cv.Revision);
        Assert.Equal(ApprovalOrigin.ConflictResolution, cv.Provenance.Origin);
        Assert.Equal(CorrectedFile, cv.Provenance.SourceImportJobId);
        Assert.Equal("dana", cv.Provenance.ApprovedBy);

        Assert.Equal(VoteHistoryEventType.ApprovedValueCorrected, evt.Type);
        Assert.Equal(("Y", "A"), (evt.OldValue, evt.NewValue));
        Assert.Equal((OriginalFile, (int?)12), (evt.OldSourceImportJobId, evt.OldSourceRowNumber));
        Assert.Equal((CorrectedFile, (int?)7), (evt.NewSourceImportJobId, evt.NewSourceRowNumber));
        Assert.Equal(("dana", conflict.Id, "Corrigendum of 18 April"), (evt.Actor, evt.ConflictId, evt.Comment));
    }

    [Fact]
    public void Second_conflict_on_the_same_fact_cannot_be_applied_after_the_first_one()
    {
        // Two different pending conflicts (Y→A from one file, Y→N from another) target BRA in vote 101.
        var cv = BrazilVotedYes();
        var first = Conflict.ForCountryBallot(cv, Ballot.Abstain, CorrectedFile, 7, T0);
        var second = Conflict.ForCountryBallot(cv, Ballot.No, Guid.NewGuid(), 3, T0);

        first.Accept("dana", T0, null);
        cv.ApplyAcceptedConflict(first, T0);

        second.Accept("omar", T0, null);
        var ex = Assert.Throws<DomainException>(() => cv.ApplyAcceptedConflict(second, T0));
        Assert.Equal(DomainErrors.ApprovedValueChanged, ex.Code);
        Assert.Equal(Ballot.Abstain, cv.Ballot);
    }

    [Fact]
    public void Pending_conflict_cannot_change_approved_data()
    {
        var cv = BrazilVotedYes();
        var conflict = Conflict.ForCountryBallot(cv, Ballot.Abstain, CorrectedFile, 7, T0);

        Assert.Throws<InvalidOperationException>(() => cv.ApplyAcceptedConflict(conflict, T0));
        Assert.Equal(Ballot.Yes, cv.Ballot);
    }

    [Fact]
    public void Conflict_is_decided_exactly_once()
    {
        var conflict = Conflict.ForCountryBallot(BrazilVotedYes(), Ballot.Abstain, CorrectedFile, 7, T0);
        conflict.Accept("dana", T0, null);

        var ex = Assert.Throws<DomainException>(() => conflict.Reject("omar", T0.AddSeconds(4), null));
        Assert.Equal(DomainErrors.ConflictAlreadyResolved, ex.Code);
        Assert.Equal(ConflictStatus.Accepted, conflict.Status);
        Assert.Equal("dana", conflict.ResolvedBy);
    }

    [Fact]
    public void Conflict_requires_an_actual_difference()
    {
        var ex = Assert.Throws<DomainException>(() => Conflict.ForCountryBallot(BrazilVotedYes(), Ballot.Yes, CorrectedFile, 7, T0));
        Assert.Equal("conflict.no_difference", ex.Code);
    }

    [Fact]
    public void Metadata_correction_goes_through_the_same_rules()
    {
        var vote = Vote.CreateFromImport(AssemblyVoteId.Create("MA/2024/009"), 79, new DateOnly(2024, 1, 27), "Biological Diversity in Coastal Zones", OriginalFile, 2, T0);
        var conflict = Conflict.ForVoteMetadata(vote, ConflictField.Title, "Biological Diversity in Coastal Zones: Revised", CorrectedFile, 5, T0);
        conflict.Accept("dana", T0, null);

        var evt = vote.ApplyAcceptedConflict(conflict, T0);

        Assert.Equal("Biological Diversity in Coastal Zones: Revised", vote.Title);
        Assert.Equal(2, vote.MetadataRevision);
        Assert.Equal(ConflictField.Title, evt.Field);
        Assert.Equal("Biological Diversity in Coastal Zones", evt.OldValue);
    }

    [Fact]
    public void Position_change_requires_the_revision_the_admin_saw()
    {
        var vote = Vote.CreateFromImport(AssemblyVoteId.Create("MA/2024/003"), 79, new DateOnly(2024, 1, 11), "Maritime Safety", OriginalFile, 2, T0);

        var evt = vote.SetPosition(Position.For, Weight.Create(4), expectedRevision: 0, "lena", T0);
        Assert.Null(evt.OldValue);
        Assert.Equal(1, vote.PositionRevision);

        var ex = Assert.Throws<DomainException>(() => vote.SetPosition(Position.Against, Weight.Create(2), expectedRevision: 0, "lena", T0));
        Assert.Equal(DomainErrors.PositionChanged, ex.Code);
        Assert.Equal(Position.For, vote.Position);
    }
}
