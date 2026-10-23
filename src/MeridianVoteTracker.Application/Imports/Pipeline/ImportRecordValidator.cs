using System.Globalization;
using MeridianVoteTracker.Domain.Imports;
using MeridianVoteTracker.Domain.MemberStates;
using MeridianVoteTracker.Domain.Votes;

namespace MeridianVoteTracker.Application.Imports.Pipeline;

/// <summary>
/// Row-level validation and normalization (ING-04, ING-13). Pure and format-independent: one
/// <see cref="RawImportRecord"/> in, one staged or Invalid <see cref="ImportRow"/> out. It never looks
/// at other rows (duplicates/contradictions) or at approved data (classification): that is Phase 3.
/// <para>
/// Normalization is limited to representation and always recorded as a note: trimming surrounding
/// whitespace and uppercasing the country code. Ballot codes, date formats and inner whitespace are
/// never changed (see DECISIONS.md). All problems of a row are reported, the first one being its reason code.
/// </para>
/// </summary>
public sealed class ImportRecordValidator(MembershipRegistry membership)
{
    private static readonly ImportRawFields NoFields = new(null, null, null, null, null, null, null, null);

    public ImportRow Validate(Guid importJobId, RawImportRecord record)
    {
        var notes = new List<string>();
        var originalText = record.OriginalText;
        if (originalText.Length > ImportRow.MaxOriginalTextLength)
        {
            originalText = originalText[..ImportRow.MaxOriginalTextLength];
            notes.Add($"original text truncated to {ImportRow.MaxOriginalTextLength} characters for storage");
            if (record.StructuralError is null)
            {
                return Invalid(importJobId, record with { OriginalText = originalText }, record.Fields ?? NoFields,
                    [new(ImportReasonCodes.RecordTooLong, $"Record is longer than {ImportRow.MaxOriginalTextLength} characters.")], notes);
            }
        }

        if (record.StructuralError is { } structural || record.Fields is null)
        {
            var problem = record.StructuralError ?? new RecordProblem(ImportReasonCodes.ColumnCount, "Record could not be mapped to columns.");
            return Invalid(importJobId, record with { OriginalText = originalText }, record.Fields ?? NoFields, [problem], notes);
        }

        var raw = record.Fields;
        var problems = new List<RecordProblem>();

        var voteIdText = Required("assembly_vote_id", raw.AssemblyVoteId, notes, problems);
        var sessionText = Required("session", raw.Session, notes, problems);
        var dateText = Required("vote_date", raw.VoteDate, notes, problems);
        var title = Required("resolution_title", raw.ResolutionTitle, notes, problems);
        var codeText = Required("country_code", raw.CountryCode, notes, problems);
        var countryName = Trimmed("country_name", raw.CountryName, notes) ?? string.Empty;
        var ballotText = Required("ballot", raw.Ballot, notes, problems);
        var publishedText = Trimmed("published_at", raw.PublishedAt, notes);

        AssemblyVoteId voteId = default;
        if (voteIdText is not null && !AssemblyVoteId.TryCreate(voteIdText, out voteId))
        {
            problems.Add(new(ImportReasonCodes.VoteIdInvalid, $"assembly_vote_id '{voteIdText}' is not a valid vote id (e.g. MA/2024/003)."));
        }

        var session = 0;
        if (sessionText is not null && (!int.TryParse(sessionText, NumberStyles.None, CultureInfo.InvariantCulture, out session) || session <= 0))
        {
            problems.Add(new(ImportReasonCodes.SessionInvalid, $"session '{sessionText}' is not a positive whole number."));
        }

        DateOnly voteDate = default;
        var hasDate = dateText is not null && DateOnly.TryParseExact(dateText, Vote.DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out voteDate);
        if (dateText is not null && !hasDate)
        {
            problems.Add(new(ImportReasonCodes.VoteDateInvalid, $"vote_date '{dateText}' is not in the format yyyy-mm-dd."));
        }

        if (title is { Length: > ImportRow.MaxTitleLength })
        {
            problems.Add(new(ImportReasonCodes.TitleTooLong, $"resolution_title is longer than {ImportRow.MaxTitleLength} characters."));
        }

        if (countryName.Length > ImportRow.MaxCountryNameLength)
        {
            problems.Add(new(ImportReasonCodes.CountryNameTooLong, $"country_name is longer than {ImportRow.MaxCountryNameLength} characters."));
        }

        CountryCode countryCode = default;
        var hasKnownCountry = false;
        if (codeText is not null)
        {
            var upper = codeText.ToUpperInvariant();
            if (!CountryCode.TryCreate(upper, out countryCode))
            {
                problems.Add(new(ImportReasonCodes.CountryCodeInvalid, $"country_code '{codeText}' is not a three-letter ISO 3166-1 alpha-3 code."));
            }
            else if (!membership.Exists(countryCode))
            {
                problems.Add(new(ImportReasonCodes.CountryCodeUnknown, $"country_code '{codeText}' is not a known member state."));
            }
            else
            {
                hasKnownCountry = true;
                if (upper != codeText)
                {
                    notes.Add($"country_code: '{codeText}' → '{upper}' (uppercased)");
                }
            }
        }

        Ballot ballot = default;
        if (ballotText is not null && !BallotCodes.TryParse(ballotText, out ballot))
        {
            problems.Add(new(ImportReasonCodes.BallotInvalid, $"ballot '{ballotText}' must be exactly one of Y, N, A, X."));
        }

        DateTimeOffset? publishedAt = null;
        if (publishedText is not null)
        {
            if (DateTimeOffset.TryParseExact(publishedText, "yyyy-MM-dd'T'HH:mm:ssK", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                publishedAt = parsed.ToUniversalTime();
            }
            else
            {
                problems.Add(new(ImportReasonCodes.PublishedAtInvalid, $"published_at '{publishedText}' is not an ISO 8601 timestamp with offset (e.g. 2024-04-02T09:00:00Z)."));
            }
        }

        if (hasKnownCountry && hasDate && !membership.WasMemberOn(countryCode, voteDate))
        {
            problems.Add(new(ImportReasonCodes.NotMemberOnDate, $"{countryCode} was not a member state on {Vote.FormatDate(voteDate)}."));
        }

        if (problems.Count > 0)
        {
            return Invalid(importJobId, record, raw, problems, notes);
        }

        var normalized = new NormalizedImportRecord(voteId, session, voteDate, title!, countryCode, countryName, ballot, publishedAt);
        return ImportRow.Staged(importJobId, record.RowNumber, record.OriginalText, raw, normalized, notes);
    }

    private static string? Required(string field, string? value, List<string> notes, List<RecordProblem> problems)
    {
        var trimmed = Trimmed(field, value, notes);
        if (trimmed is null)
        {
            problems.Add(new(ImportReasonCodes.Required, $"{field} is required."));
        }

        return trimmed;
    }

    /// <returns>The trimmed value, or null when missing/blank.</returns>
    private static string? Trimmed(string field, string? value, List<string> notes)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Length != value.Length)
        {
            notes.Add($"{field}: surrounding whitespace removed");
        }

        return trimmed;
    }

    private static ImportRow Invalid(Guid importJobId, RawImportRecord record, ImportRawFields raw, IReadOnlyList<RecordProblem> problems, List<string> notes)
    {
        var message = string.Join(" ", problems.Select(p => p.Message));
        if (message.Length > ImportRow.MaxMessageLength)
        {
            message = message[..(ImportRow.MaxMessageLength - 1)] + "…";
        }

        return ImportRow.Invalid(importJobId, record.RowNumber, record.OriginalText, raw, problems[0].Code, message, notes);
    }
}
