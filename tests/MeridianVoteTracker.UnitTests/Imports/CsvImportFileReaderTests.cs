using System.Text;
using MeridianVoteTracker.Application.Imports.Pipeline;
using MeridianVoteTracker.Infrastructure.Imports.Csv;

namespace MeridianVoteTracker.UnitTests.Imports;

public class CsvImportFileReaderTests
{
    private const string Header = "assembly_vote_id,session,vote_date,resolution_title,country_code,country_name,ballot,published_at";
    private const string Row = "MA/2024/117,79,2024-03-12,\"Maritime Safety, Navigation and Trade\",FRA,France,Y,2024-03-14T09:00:00Z";

    private static async Task<List<RawImportRecord>> ReadAsync(string content, bool withBom = false)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        if (withBom)
        {
            bytes = [.. Encoding.UTF8.GetPreamble(), .. bytes];
        }

        var records = new List<RawImportRecord>();
        await foreach (var record in new CsvImportFileReader().ReadAsync(new MemoryStream(bytes), CancellationToken.None))
        {
            records.Add(record);
        }

        return records;
    }

    [Fact]
    public async Task Maps_fields_by_header_and_keeps_quoted_commas()
    {
        var record = Assert.Single(await ReadAsync($"{Header}\n{Row}\n"));

        Assert.Null(record.StructuralError);
        Assert.Equal(2, record.RowNumber);
        Assert.Equal("Maritime Safety, Navigation and Trade", record.Fields!.ResolutionTitle);
        Assert.Equal("FRA", record.Fields.CountryCode);
        Assert.Equal(Row, record.OriginalText);
    }

    [Fact]
    public async Task Handles_utf8_bom_and_crlf_line_endings()
    {
        // q1 starts with a BOM; rev2 uses CRLF.
        var records = await ReadAsync($"{Header}\r\n{Row}\r\n{Row.Replace("FRA,France", "BRA,Brazil")}\r\n", withBom: true);

        Assert.Equal([2, 3], records.Select(r => r.RowNumber));
        Assert.All(records, r => Assert.Null(r.StructuralError));
        Assert.Equal("MA/2024/117", records[0].Fields!.AssemblyVoteId); // BOM not glued to the first column
        Assert.DoesNotContain('\r', records[1].OriginalText);
    }

    [Fact]
    public async Task Columns_in_a_different_order_are_mapped_by_name()
    {
        var record = Assert.Single(await ReadAsync(
            "ballot,country_code,assembly_vote_id,session,vote_date,resolution_title,country_name,published_at\nN,JPN,MA/2024/001,79,2024-01-12,T,Japan,\n"));

        Assert.Equal(("N", "JPN", "MA/2024/001"), (record.Fields!.Ballot, record.Fields.CountryCode, record.Fields.AssemblyVoteId));
    }

    [Fact]
    public async Task Wrong_column_count_is_reported_and_reading_continues()
    {
        // q1 line 3139: "...,AGO;N" (wrong delimiter).
        var records = await ReadAsync($"{Header}\nMA/2024/022,79,2024-02-17,Arms Trade Transparency,AGO;N\n{Row}\n");

        Assert.Equal(ImportReasonCodes.ColumnCount, records[0].StructuralError!.Code);
        Assert.Null(records[0].Fields);
        Assert.Equal("MA/2024/022,79,2024-02-17,Arms Trade Transparency,AGO;N", records[0].OriginalText);
        Assert.Null(records[1].StructuralError);
        Assert.Equal(3, records[1].RowNumber);
    }

    [Fact]
    public async Task Stray_quote_is_reported_as_malformed_quoting()
    {
        var records = await ReadAsync($"{Header}\nMA/2024/001,79,2024-01-12,Bad \"quote,FRA,France,Y,2024-04-02T09:00:00Z\n{Row}\n");

        Assert.Equal(ImportReasonCodes.MalformedQuoting, records[0].StructuralError!.Code);
        Assert.Null(records[1].StructuralError);
    }

    [Fact]
    public async Task Raw_values_are_not_trimmed_or_case_folded()
    {
        var record = Assert.Single(await ReadAsync($"{Header}\nMA/2024/018,79,2024-02-10,Title, deu ,Germany,Yes,\n"));

        Assert.Equal(" deu ", record.Fields!.CountryCode);
        Assert.Equal("Yes", record.Fields.Ballot);
        Assert.Equal(string.Empty, record.Fields.PublishedAt);
    }

    [Fact]
    public async Task Row_numbers_are_physical_lines_even_with_blank_lines()
    {
        var records = await ReadAsync($"{Header}\n{Row}\n\n{Row}\n");

        Assert.Equal([2, 4], records.Select(r => r.RowNumber));
    }

    [Fact]
    public async Task Missing_required_column_fails_the_whole_file()
    {
        var ex = await Assert.ThrowsAsync<ImportFileException>(() => ReadAsync("assembly_vote_id,session,vote_date\nMA/2024/001,79,2024-01-12\n"));

        Assert.Equal("file.missing_columns", ex.Code);
        Assert.Contains("country_code", ex.Message);
    }

    [Fact]
    public async Task Header_only_file_yields_no_records()
    {
        Assert.Empty(await ReadAsync($"{Header}\n"));
    }

    [Theory]
    [InlineData("export.csv", true)]
    [InlineData("EXPORT.CSV", true)]
    [InlineData("export.xlsx", false)]
    [InlineData("export.txt", false)]
    public void Detects_csv_by_extension(string fileName, bool expected) =>
        Assert.Equal(expected, new CsvImportFileReader().CanRead(fileName, "a,b"u8));

    [Fact]
    public void Rejects_binary_content_with_csv_extension() =>
        Assert.False(new CsvImportFileReader().CanRead("export.csv", [0x50, 0x4B, 0x03, 0x04, 0x00]));
}
