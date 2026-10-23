using System.Net;
using System.Text;
using MeridianVoteTracker.Application.Imports.GetImportStatus;
using MeridianVoteTracker.Application.Imports.Pipeline;
using MeridianVoteTracker.Domain.Imports;
using MeridianVoteTracker.Domain.MemberStates;
using MeridianVoteTracker.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace MeridianVoteTracker.IntegrationTests.Imports;

[Collection(ImportCollection.Name)]
public class ImportUploadTests(ImportApiFactory factory)
{
    private readonly ImportTestClient _dana = new(factory);
    private readonly TestData _data = new(factory);

    [Fact]
    public async Task Real_q1_export_is_parsed_validated_and_staged_with_every_row_accounted_for()
    {
        var bytes = await File.ReadAllBytesAsync(AssignmentData.PathOf("assembly_export_2024_q1.csv"));

        var status = await _dana.UploadAndWaitAsync("assembly_export_2024_q1.csv", bytes);

        Assert.Equal(ImportProgressState.AwaitingClassification, status.State);
        Assert.Equal((5896, 5896, 11), (status.TotalRows!.Value, status.StagedRows, status.InvalidRows));
        Assert.Equal("dana", status.UploadedBy);
        Assert.Equal(bytes.Length, status.SizeBytes);

        await using var scope = _data.OpenScope();
        var rows = await scope.Db.ImportRows.AsNoTracking().Where(r => r.ImportJobId == status.Id).ToDictionaryAsync(r => r.RowNumber);
        Assert.Equal(5896, rows.Count);
        Assert.Equal(Enumerable.Range(2, 5896), rows.Keys.Order());

        // Every planted problem of the original file, at its physical line (see DECISIONS.md / data notes).
        AssertInvalid(rows[2143], ImportReasonCodes.CountryCodeUnknown, rawCountry: "FRN");
        AssertInvalid(rows[2842], ImportReasonCodes.BallotInvalid, rawBallot: "Yes");
        AssertInvalid(rows[2846], ImportReasonCodes.Required, rawBallot: "");
        AssertInvalid(rows[3139], ImportReasonCodes.ColumnCount);
        Assert.EndsWith("AGO;N", rows[3139].OriginalText);
        AssertInvalid(rows[4854], ImportReasonCodes.NotMemberOnDate, rawCountry: "NZL");
        Assert.All(Enumerable.Range(3880, 6), line => AssertInvalid(rows[line], ImportReasonCodes.VoteDateInvalid));
        Assert.Equal("21/02/2024", rows[3880].RawVoteDate);

        // Normalized, not rejected, and the normalization is visible.
        var deu = rows[2594];
        Assert.Null(deu.Classification);
        Assert.Equal(("deu", CountryCode.Create("DEU")), (deu.RawCountryCode, deu.CountryCode!.Value));
        Assert.Equal(["country_code: 'deu' → 'DEU' (uppercased)"], deu.NormalizationNotes);

        // Exact duplicates and contradictions are row-valid; they are classified in Phase 3.
        Assert.Null(rows[1045].Classification);
        Assert.Equal(rows[896].OriginalText, rows[1045].OriginalText);
    }

    [Fact]
    public async Task Upload_returns_202_with_location_before_processing_finishes()
    {
        var response = await _dana.UploadAsync("small.csv", Encoding.UTF8.GetBytes(SmallCsv));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Matches(@"/api/imports/[0-9a-f\-]{36}$", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Upload_requires_a_user()
    {
        var anonymous = factory.CreateClient();
        var response = await anonymous.PostAsync("/api/imports", new MultipartFormDataContent());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Unsupported_format_is_rejected_with_415_and_nothing_is_queued()
    {
        var jobsBefore = await CountJobsAsync();

        var response = await _dana.UploadAsync("notes.txt", "hello"u8.ToArray());

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Contains("file.unsupported_format", await response.Content.ReadAsStringAsync());
        Assert.Equal(jobsBefore, await CountJobsAsync());
        Assert.Empty(Directory.GetFiles(factory.StorageRoot, "*.txt"));
    }

    [Fact]
    public async Task Empty_file_is_a_validation_error_on_the_file_field()
    {
        var response = await _dana.UploadAsync("empty.csv", []);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"file\"", body);
        Assert.Contains("file.empty", body);
    }

    [Fact]
    public async Task File_without_the_required_header_fails_without_retry()
    {
        var status = await _dana.UploadAndWaitAsync("wrong.csv", "vote,country,choice\nMA/2024/001,FRA,Y\n"u8.ToArray());

        Assert.Equal(ImportProgressState.Failed, status.State);
        Assert.Equal(1, status.Attempts);
        Assert.StartsWith("file.missing_columns", status.Error);
    }

    [Fact]
    public async Task Unknown_import_returns_404_problem_details()
    {
        var response = await factory.CreateClient().SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/api/imports/{Guid.NewGuid()}")
        {
            Headers = { { "X-User-Id", "omar" } },
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    private const string SmallCsv = """
        assembly_vote_id,session,vote_date,resolution_title,country_code,country_name,ballot,published_at
        MA/2024/101,79,2024-01-11,Maritime Safety,BRA,Brazil,Y,2024-04-02T09:00:00Z
        """;

    private static void AssertInvalid(ImportRow row, string reasonCode, string? rawCountry = null, string? rawBallot = null)
    {
        Assert.Equal(ImportClassification.Invalid, row.Classification);
        Assert.Equal(reasonCode, row.ReasonCode);
        Assert.False(string.IsNullOrEmpty(row.Message));
        if (rawCountry is not null)
        {
            Assert.Equal(rawCountry, row.RawCountryCode);
        }

        if (rawBallot is not null)
        {
            Assert.Equal(rawBallot, row.RawBallot);
        }
    }

    private async Task<int> CountJobsAsync()
    {
        await using var scope = _data.OpenScope();
        return await scope.Db.ImportJobs.CountAsync();
    }
}
