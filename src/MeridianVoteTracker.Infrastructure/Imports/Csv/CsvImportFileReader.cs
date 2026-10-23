using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using MeridianVoteTracker.Application.Imports.Pipeline;
using MeridianVoteTracker.Application.Imports.Ports;
using MeridianVoteTracker.Domain.Imports;

namespace MeridianVoteTracker.Infrastructure.Imports.Csv;

/// <summary>
/// Reads the Assembly export format (Appendix A) with CsvHelper. Everything CSV-specific lives here:
/// encoding and BOM, CRLF/LF, RFC 4180 quoting, header-to-column mapping, physical line numbers.
/// Records are streamed (never the whole file in memory) and every record is yielded, including broken
/// ones, which carry a structural error instead of throwing (ING-07).
/// </summary>
internal sealed class CsvImportFileReader : IImportFileReader
{
    public const string FormatName = "csv";

    internal static readonly string[] Columns =
        ["assembly_vote_id", "session", "vote_date", "resolution_title", "country_code", "country_name", "ballot", "published_at"];

    public string Format => FormatName;

    public bool CanRead(string fileName, ReadOnlySpan<byte> head) =>
        string.Equals(Path.GetExtension(fileName), ".csv", StringComparison.OrdinalIgnoreCase) && !head.Contains((byte)0);

    public async IAsyncEnumerable<RawImportRecord> ReadAsync(Stream content, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? badData = null;
        var configuration = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            TrimOptions = TrimOptions.None,      // raw values are preserved; trimming is a recorded normalization
            IgnoreBlankLines = true,
            DetectColumnCountChanges = false,    // column count is checked per record below, not thrown
            MissingFieldFound = null,
            BadDataFound = args => badData ??= args.RawRecord,
        };

        using var text = new StreamReader(content, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true, bufferSize: 64 * 1024, leaveOpen: true);
        using var csv = new CsvReader(text, configuration);

        if (!await csv.ReadAsync())
        {
            throw new ImportFileException("file.no_header", "The file is empty: no header row.");
        }

        csv.ReadHeader();
        var columnIndexes = MapHeader(csv.HeaderRecord ?? []);
        var expectedFieldCount = csv.HeaderRecord!.Length;

        while (await csv.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parser = csv.Parser;
            var originalText = parser.RawRecord.TrimEnd('\r', '\n');
            var rowNumber = parser.RawRow - CountLineBreaks(originalText);
            var fields = parser.Record ?? [];

            RawImportRecord record;
            if (originalText.Contains('\0'))
            {
                record = RawImportRecord.Malformed(rowNumber, originalText.Replace('\0', '�'),
                    new(ImportReasonCodes.NulCharacter, "Record contains a NUL character (shown as �)."));
            }
            else if (badData is not null)
            {
                record = RawImportRecord.Malformed(rowNumber, originalText,
                    new(ImportReasonCodes.MalformedQuoting, "Record has malformed quoting (a quote inside an unquoted field or an unterminated quoted field)."));
            }
            else if (fields.Length != expectedFieldCount)
            {
                record = RawImportRecord.Malformed(rowNumber, originalText,
                    new(ImportReasonCodes.ColumnCount, $"Expected {expectedFieldCount} fields, found {fields.Length}."));
            }
            else
            {
                record = RawImportRecord.Mapped(rowNumber, originalText, new ImportRawFields(
                    fields[columnIndexes[0]], fields[columnIndexes[1]], fields[columnIndexes[2]], fields[columnIndexes[3]],
                    fields[columnIndexes[4]], fields[columnIndexes[5]], fields[columnIndexes[6]], fields[columnIndexes[7]]));
            }

            badData = null;
            yield return record;
        }
    }

    /// <summary>Columns are matched by name (order-independent, case-insensitive); all eight are required.</summary>
    private static int[] MapHeader(string[] header)
    {
        var positions = header
            .Select((name, index) => (Name: name.Trim(), Index: index))
            .GroupBy(h => h.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Index, StringComparer.OrdinalIgnoreCase);

        var missing = Columns.Where(c => !positions.ContainsKey(c)).ToList();
        if (missing.Count > 0)
        {
            throw new ImportFileException("file.missing_columns",
                $"The header is missing required column(s): {string.Join(", ", missing)}. Found: {string.Join(", ", header)}.");
        }

        return Columns.Select(c => positions[c]).ToArray();
    }

    private static int CountLineBreaks(string text) => text.Count(c => c == '\n');
}
