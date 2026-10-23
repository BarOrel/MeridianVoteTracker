namespace MeridianVoteTracker.Application.Imports.Pipeline;

/// <summary>Stable reason codes stored on rejected rows; the report groups and filters by them.</summary>
public static class ImportReasonCodes
{
    public const string ColumnCount = "structure.column_count";
    public const string MalformedQuoting = "structure.malformed_quoting";
    public const string NulCharacter = "structure.nul_character";
    public const string RecordTooLong = "structure.record_too_long";

    public const string Required = "field.required";
    public const string VoteIdInvalid = "assembly_vote_id.invalid";
    public const string SessionInvalid = "session.invalid";
    public const string VoteDateInvalid = "vote_date.invalid_format";
    public const string TitleTooLong = "title.too_long";
    public const string CountryCodeInvalid = "country_code.invalid_format";
    public const string CountryCodeUnknown = "country_code.unknown";
    public const string CountryNameTooLong = "country_name.too_long";
    public const string BallotInvalid = "ballot.invalid";
    public const string PublishedAtInvalid = "published_at.invalid";
    public const string NotMemberOnDate = "membership.not_member_on_date";
}
