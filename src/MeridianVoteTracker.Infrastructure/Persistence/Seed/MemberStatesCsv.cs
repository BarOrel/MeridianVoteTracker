using System.Globalization;

namespace MeridianVoteTracker.Infrastructure.Persistence.Seed;

internal sealed record MemberStateSeedRow(int LineNumber, string Code, string Name, DateOnly From, DateOnly? To);

/// <summary>
/// Strict parser for the reference file member_states.csv (country_code,country_name,member_from,member_to).
/// Reference data is trusted but verified: any malformed line stops start-up with its line number,
/// rather than seeding a partial or wrong membership table. The file has no quoted fields; a quote
/// is rejected so the simple split can never mis-parse.
/// </summary>
internal static class MemberStatesCsv
{
    private const string ExpectedHeader = "country_code,country_name,member_from,member_to";

    public static IReadOnlyList<MemberStateSeedRow> Parse(TextReader reader)
    {
        var header = reader.ReadLine()?.TrimStart('﻿');
        if (header != ExpectedHeader)
        {
            throw new FormatException($"member_states.csv: expected header '{ExpectedHeader}', found '{header}'.");
        }

        var rows = new List<MemberStateSeedRow>();
        var lineNumber = 1;
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            if (line.Length == 0)
            {
                continue;
            }

            var fields = line.Split(',');
            if (fields.Length != 4 || line.Contains('"'))
            {
                throw new FormatException($"member_states.csv line {lineNumber}: expected 4 unquoted fields: '{line}'.");
            }

            rows.Add(new MemberStateSeedRow(lineNumber, fields[0], fields[1], ParseDate(fields[2], lineNumber), fields[3].Length == 0 ? null : ParseDate(fields[3], lineNumber)));
        }

        return rows;
    }

    private static DateOnly ParseDate(string value, int lineNumber) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : throw new FormatException($"member_states.csv line {lineNumber}: invalid date '{value}'.");
}
