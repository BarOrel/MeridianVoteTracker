using MeridianVoteTracker.Application.Imports.Pipeline;
using MeridianVoteTracker.Domain.Imports;
using MeridianVoteTracker.Domain.MemberStates;
using MeridianVoteTracker.Domain.Votes;

namespace MeridianVoteTracker.UnitTests.Imports;

public class ImportRecordValidatorTests
{
    private static readonly Guid JobId = Guid.NewGuid();
    private static readonly ImportRecordValidator Validator = new(Registry());

    private static MembershipRegistry Registry()
    {
        MemberState State(string code, string from, string? to = null)
        {
            var state = new MemberState(CountryCode.Create(code), code);
            state.AddPeriod(DateOnly.Parse(from), to is null ? null : DateOnly.Parse(to));
            return state;
        }

        return new MembershipRegistry([State("FRA", "1995-01-01"), State("DEU", "1995-01-01"), State("NZL", "1995-01-01", "2021-06-30")]);
    }

    private static ImportRawFields Fields(
        string? voteId = "MA/2024/117", string? session = "79", string? date = "2024-03-12", string? title = "Maritime Safety",
        string? code = "FRA", string? name = "France", string? ballot = "Y", string? published = "2024-03-14T09:00:00Z") =>
        new(voteId, session, date, title, code, name, ballot, published);

    private static ImportRow Validate(ImportRawFields fields, int row = 2) =>
        Validator.Validate(JobId, RawImportRecord.Mapped(row, "original line", fields));

    [Fact]
    public void Valid_row_is_staged_with_typed_values_and_no_classification_yet()
    {
        var row = Validate(Fields());

        Assert.Null(row.Classification);
        Assert.Equal(AssemblyVoteId.Create("MA/2024/117"), row.AssemblyVoteId);
        Assert.Equal((79, new DateOnly(2024, 3, 12), Ballot.Yes), (row.Session!.Value, row.VoteDate!.Value, row.Ballot!.Value));
        Assert.Equal(new DateTimeOffset(2024, 3, 14, 9, 0, 0, TimeSpan.Zero), row.PublishedAt);
        Assert.Empty(row.NormalizationNotes);
        Assert.Equal("original line", row.OriginalText);
    }

    [Fact]
    public void Lowercase_country_code_is_normalized_with_a_visible_note_and_raw_value_kept()
    {
        var row = Validate(Fields(code: "deu", name: "Germany"));

        Assert.Equal(CountryCode.Create("DEU"), row.CountryCode);
        Assert.Equal("deu", row.RawCountryCode);
        Assert.Contains("country_code: 'deu' → 'DEU' (uppercased)", row.NormalizationNotes);
    }

    [Fact]
    public void Surrounding_whitespace_is_trimmed_with_a_note_but_inner_whitespace_is_kept()
    {
        var row = Validate(Fields(title: " Nuclear  Non-Proliferation "));

        Assert.Equal("Nuclear  Non-Proliferation", row.Title);
        Assert.Contains("resolution_title: surrounding whitespace removed", row.NormalizationNotes);
    }

    [Theory]
    [InlineData("Yes")]
    [InlineData("y")]
    [InlineData("0")]
    public void Ballot_synonyms_are_invalid(string ballot)
    {
        var row = Validate(Fields(ballot: ballot));

        Assert.Equal(ImportClassification.Invalid, row.Classification);
        Assert.Equal(ImportReasonCodes.BallotInvalid, row.ReasonCode);
        Assert.Equal(ballot, row.RawBallot);
        Assert.Null(row.AssemblyVoteId); // no normalized values on invalid rows
    }

    [Fact]
    public void Empty_ballot_is_a_missing_required_field()
    {
        var row = Validate(Fields(ballot: ""));

        Assert.Equal(ImportReasonCodes.Required, row.ReasonCode);
        Assert.Contains("ballot is required", row.Message);
    }

    [Theory]
    [InlineData("21/02/2024")]
    [InlineData("21-02-2024")]
    [InlineData("2024-02-30")]
    public void Only_iso_dates_are_accepted(string date) =>
        Assert.Equal(ImportReasonCodes.VoteDateInvalid, Validate(Fields(date: date)).ReasonCode);

    [Fact]
    public void Unknown_country_code_is_invalid() =>
        Assert.Equal(ImportReasonCodes.CountryCodeUnknown, Validate(Fields(code: "FRN")).ReasonCode);

    [Fact]
    public void State_that_was_not_a_member_on_the_vote_date_is_invalid()
    {
        var row = Validate(Fields(code: "NZL", date: "2024-02-28"));

        Assert.Equal(ImportReasonCodes.NotMemberOnDate, row.ReasonCode);
        Assert.Contains("NZL was not a member state on 2024-02-28", row.Message);
    }

    [Fact]
    public void All_problems_are_reported_first_one_is_the_reason_code()
    {
        var row = Validate(Fields(session: "x", code: "FRN", ballot: "?"));

        Assert.Equal(ImportReasonCodes.SessionInvalid, row.ReasonCode);
        Assert.Contains("country_code 'FRN'", row.Message);
        Assert.Contains("ballot '?'", row.Message);
    }

    [Fact]
    public void Structural_error_from_the_reader_becomes_an_invalid_row_with_original_text()
    {
        var record = RawImportRecord.Malformed(3139, "MA/2024/022,79,2024-02-17,Arms,AGO;N", new(ImportReasonCodes.ColumnCount, "Expected 8 fields, found 5."));

        var row = Validator.Validate(JobId, record);

        Assert.Equal(ImportClassification.Invalid, row.Classification);
        Assert.Equal(ImportReasonCodes.ColumnCount, row.ReasonCode);
        Assert.Equal("MA/2024/022,79,2024-02-17,Arms,AGO;N", row.OriginalText);
        Assert.Equal(3139, row.RowNumber);
    }

    [Fact]
    public void Published_at_is_optional_but_must_be_iso_when_present()
    {
        Assert.Null(Validate(Fields(published: "")).Classification);
        Assert.Equal(ImportReasonCodes.PublishedAtInvalid, Validate(Fields(published: "02/04/2024")).ReasonCode);
    }

    [Fact]
    public void Values_too_long_for_storage_are_invalid_instead_of_failing_the_batch()
    {
        Assert.Equal(ImportReasonCodes.TitleTooLong, Validate(Fields(title: new string('t', ImportRow.MaxTitleLength + 1))).ReasonCode);

        var huge = RawImportRecord.Mapped(5, new string('x', ImportRow.MaxOriginalTextLength + 10), Fields());
        var row = Validator.Validate(JobId, huge);
        Assert.Equal(ImportReasonCodes.RecordTooLong, row.ReasonCode);
        Assert.Equal(ImportRow.MaxOriginalTextLength, row.OriginalText.Length);
    }
}
