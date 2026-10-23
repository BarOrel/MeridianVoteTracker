using MeridianVoteTracker.Domain.Conflicts;
using MeridianVoteTracker.Domain.Imports;
using MeridianVoteTracker.Domain.MemberStates;
using MeridianVoteTracker.Domain.Votes;

namespace MeridianVoteTracker.UnitTests.Domain;

/// <summary>Follows the worked example of the brief (§7.2) plus the edge cases around it.</summary>
public class VoteImportClassifierTests
{
    private static readonly DateTimeOffset Now = new(2024, 4, 22, 15, 30, 0, TimeSpan.Zero);
    private static readonly Guid OriginalFile = Guid.NewGuid();
    private static readonly Guid CorrectedFile = Guid.NewGuid();
    private static readonly AssemblyVoteId Vote101 = AssemblyVoteId.Create("MA/2024/101");
    private const string Title = "Maritime Safety, Navigation and Trade";

    private static readonly VoteImportClassifier Classifier = new(Registry());

    private static MembershipRegistry Registry() => new(
        new[] { ("BRA", "Brazil"), ("IND", "India"), ("FRA", "France"), ("JPN", "Japan"), ("TUR", "Türkiye") }.Select(s =>
        {
            var state = new MemberState(CountryCode.Create(s.Item1), s.Item2);
            state.AddPeriod(new DateOnly(1995, 1, 1), null);
            return state;
        }));

    private static ImportRow Row(int number, string code, string ballot, string title = Title, string? rawCode = null, string? name = null, string date = "2024-01-11")
    {
        var raw = new ImportRawFields(Vote101.Value, "79", date, title, rawCode ?? code, name ?? code, ballot, "2024-04-02T09:00:00Z");
        BallotCodes.TryParse(ballot, out var parsed);
        var normalized = new NormalizedImportRecord(Vote101, 79, DateOnly.Parse(date), title, CountryCode.Create(code), name ?? code, parsed, Now);
        return ImportRow.Staged(Guid.NewGuid(), number, $"line {number}", raw, normalized, rawCode is null ? [] : ["country_code uppercased"]);
    }

    private static ApprovedVoteState Approved(params (string Code, Ballot Ballot)[] ballots)
    {
        var vote = Vote.CreateFromImport(Vote101, 79, new DateOnly(2024, 1, 11), Title, OriginalFile, 2, Now.AddDays(-1));
        return new ApprovedVoteState(vote, ballots.ToDictionary(
            b => CountryCode.Create(b.Code),
            b => CountryVote.CreateFromImport(vote.Id, CountryCode.Create(b.Code), b.Ballot, OriginalFile, 2, Now.AddDays(-1))));
    }

    private static VoteClassificationResult Classify(IReadOnlyList<ImportRow> rows, ApprovedVoteState? approved = null, params Conflict[] known) =>
        Classifier.Classify(CorrectedFile, Vote101, rows, approved, known, Now);

    [Fact]
    public void New_vote_stores_every_unambiguous_row()
    {
        var rows = new[] { Row(1, "BRA", "Y"), Row(2, "IND", "Y") };

        var result = Classify(rows);

        Assert.All(rows, r => Assert.Equal(ImportClassification.New, r.Classification));
        Assert.Equal(new NewVoteSpec(79, new DateOnly(2024, 1, 11), Title, 1), result.NewVote);
        Assert.Equal(2, result.NewBallots.Count);
        Assert.Equal(VoteImportOutcome.NewVote, result.Outcome.Outcome);
    }

    [Fact]
    public void Exact_duplicate_is_ignored_and_points_to_the_first_occurrence()
    {
        var rows = new[] { Row(3, "JPN", "Y"), Row(4, "JPN", "Y") };

        var result = Classify(rows);

        Assert.Equal(ImportClassification.New, rows[0].Classification);
        Assert.Equal((ImportClassification.Duplicate, DuplicateKind.Exact, 3), (rows[1].Classification, rows[1].DuplicateKind, rows[1].DuplicateOfRowNumber));
        Assert.Single(result.NewBallots);
    }

    [Fact]
    public void Duplicate_after_normalization_is_distinguished_from_an_exact_one()
    {
        var rows = new[] { Row(10, "JPN", "Y"), Row(11, "JPN", "Y", rawCode: "jpn") };

        Classify(rows);

        Assert.Equal(DuplicateKind.AfterNormalization, rows[1].DuplicateKind);
        Assert.Equal("duplicate.after_normalization", rows[1].ReasonCode);
    }

    [Fact]
    public void Same_state_with_different_ballots_is_a_contradiction_and_neither_value_is_stored()
    {
        var rows = new[] { Row(5, "FRA", "Y"), Row(6, "FRA", "N"), Row(7, "BRA", "Y") };

        var result = Classify(rows);

        Assert.All(rows[..2], r => Assert.Equal(ImportClassification.Contradiction, r.Classification));
        Assert.Contains("row 5: Y, row 6: N", rows[0].Message);
        Assert.Equal([CountryCode.Create("BRA")], result.NewBallots.Select(b => b.Country));
        Assert.Equal(2, result.Outcome.ContradictionRows);
    }

    [Fact]
    public void Rows_that_disagree_on_vote_details_block_the_whole_vote()
    {
        // q1 MA/2024/025: one row with a double space, one with "(draft)".
        var rows = new[] { Row(1, "BRA", "Y"), Row(2, "IND", "N", title: Title + " (draft)"), Row(3, "FRA", "Y") };

        var result = Classify(rows);

        Assert.All(rows, r => Assert.Equal(ImportClassification.Contradiction, r.Classification));
        Assert.Null(result.NewVote);
        Assert.Empty(result.NewBallots);
        Assert.Equal(VoteImportOutcome.BlockedByContradiction, result.Outcome.Outcome);
    }

    [Fact]
    public void Corrected_file_follows_the_brief_example()
    {
        // Approved: BRA Y, IND Y. Corrected file: BRA A (conflict), IND Y (unchanged), FRA N (missing state: new), new title.
        var rows = new[] { Row(1, "BRA", "A", title: Title + ": Revised"), Row(2, "IND", "Y", title: Title + ": Revised"), Row(3, "FRA", "N", title: Title + ": Revised") };

        var result = Classify(rows, Approved(("BRA", Ballot.Yes), ("IND", Ballot.Yes)));

        Assert.Equal(ImportClassification.Conflict, rows[0].Classification);
        Assert.Equal("conflict.created", rows[0].ReasonCode);
        Assert.Equal(ImportClassification.Unchanged, rows[1].Classification);
        Assert.Equal((ImportClassification.New, "new.country_vote"), (rows[2].Classification, rows[2].ReasonCode));
        Assert.Null(result.NewVote);
        Assert.Equal([CountryCode.Create("FRA")], result.NewBallots.Select(b => b.Country));

        Assert.Equal(2, result.NewConflicts.Count);
        var ballot = Assert.Single(result.NewConflicts, c => c.Kind == ConflictKind.CountryBallot);
        Assert.Equal(("Y", "A", 1), (ballot.ApprovedValueAtDetection, ballot.ProposedValue, ballot.ExpectedRevision));
        Assert.Equal(ballot.Id, rows[0].ConflictId);
        var title = Assert.Single(result.NewConflicts, c => c.Kind == ConflictKind.VoteMetadata);
        Assert.Equal((ConflictField.Title, Title + ": Revised"), (title.Field, title.ProposedValue));
        Assert.Equal(VoteImportOutcome.ConflictsDetected, result.Outcome.Outcome);
    }

    [Fact]
    public void Missing_states_of_a_partial_vote_are_added_as_new_not_as_conflicts()
    {
        var rows = new[] { Row(1, "BRA", "Y"), Row(2, "FRA", "X") };

        var result = Classify(rows, Approved(("BRA", Ballot.Yes)));

        Assert.Equal(ImportClassification.Unchanged, rows[0].Classification);
        Assert.Equal(ImportClassification.New, rows[1].Classification);
        Assert.Empty(result.NewConflicts);
        Assert.Equal(VoteImportOutcome.CountriesAdded, result.Outcome.Outcome);
    }

    [Fact]
    public void Proposal_already_pending_is_linked_not_opened_again()
    {
        var approved = Approved(("BRA", Ballot.Yes));
        var pending = Conflict.ForCountryBallot(approved.CountryVotes[CountryCode.Create("BRA")], Ballot.Abstain, CorrectedFile, 1, Now.AddHours(-1));
        var rows = new[] { Row(1, "BRA", "A") };

        var result = Classify(rows, approved, pending);

        Assert.Empty(result.NewConflicts);
        Assert.Equal((ImportClassification.Conflict, "conflict.already_pending", (Guid?)pending.Id), (rows[0].Classification, rows[0].ReasonCode, rows[0].ConflictId));
        Assert.Equal(0, result.Outcome.ConflictsCreated);
    }

    [Fact]
    public void Proposal_rejected_against_the_same_approved_value_is_not_reopened()
    {
        var approved = Approved(("BRA", Ballot.Yes));
        var rejected = Conflict.ForCountryBallot(approved.CountryVotes[CountryCode.Create("BRA")], Ballot.Abstain, CorrectedFile, 1, Now.AddHours(-1));
        rejected.Reject("dana", Now.AddMinutes(-30), "Assembly confirmed YES");
        var rows = new[] { Row(1, "BRA", "A") };

        var result = Classify(rows, approved, rejected);

        Assert.Empty(result.NewConflicts);
        Assert.Equal(("conflict.previously_rejected", (Guid?)rejected.Id), (rows[0].ReasonCode, rows[0].ConflictId));
    }

    [Fact]
    public void Different_proposal_than_the_pending_one_opens_its_own_conflict()
    {
        var approved = Approved(("BRA", Ballot.Yes));
        var pending = Conflict.ForCountryBallot(approved.CountryVotes[CountryCode.Create("BRA")], Ballot.Abstain, CorrectedFile, 1, Now.AddHours(-1));
        var rows = new[] { Row(1, "BRA", "N") };

        var result = Classify(rows, approved, pending);

        var opened = Assert.Single(result.NewConflicts);
        Assert.Equal("N", opened.ProposedValue);
    }

    [Fact]
    public void Other_country_name_for_the_same_code_is_noted_not_a_conflict()
    {
        var rows = new[] { Row(1, "TUR", "Y", name: "Turkey") };

        var result = Classify(rows, Approved(("TUR", Ballot.Yes)));

        Assert.Equal(ImportClassification.Unchanged, rows[0].Classification);
        Assert.Empty(result.NewConflicts);
        Assert.Contains(result.Outcome.Notes, n => n.Contains("\"Turkey\"") && n.Contains("Türkiye"));
    }

    [Fact]
    public void Every_row_ends_with_exactly_one_classification_regardless_of_file_order()
    {
        var rows = new[] { Row(9, "FRA", "N"), Row(2, "BRA", "Y"), Row(5, "FRA", "Y"), Row(7, "BRA", "Y"), Row(1, "IND", "A") };

        Classify(rows, Approved(("IND", Ballot.Yes)));

        Assert.All(rows, r => Assert.NotNull(r.Classification));
        Assert.Equal(ImportClassification.Duplicate, rows[3].Classification); // row 7 duplicates row 2 (earlier in file)
        Assert.Equal(2, rows[3].DuplicateOfRowNumber);
    }
}
