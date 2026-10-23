using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeridianVoteTracker.Api.Auth;
using MeridianVoteTracker.Application.Imports.GetImportReport;
using MeridianVoteTracker.Application.Imports.GetImportStatus;

namespace MeridianVoteTracker.IntegrationTests.Infrastructure;

/// <summary>Drives imports through the real HTTP API, as the UI does.</summary>
internal sealed class ImportTestClient(ApiFactory factory, string userId = "dana")
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static readonly ImportProgressState[] Settled =
    [
        ImportProgressState.Completed, ImportProgressState.CompletedWithIssues, ImportProgressState.Failed,
    ];

    private readonly HttpClient _client = CreateClient(factory, userId);

    public Task<HttpResponseMessage> UploadAsync(string fileName, byte[] content)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(file, "file", fileName);
        return _client.PostAsync("/api/imports", form);
    }

    public async Task<ImportStatusDto> UploadAndWaitAsync(string fileName, byte[] content, TimeSpan? timeout = null)
    {
        var response = await UploadAsync(fileName, content);
        response.EnsureSuccessStatusCode();
        var accepted = (await response.Content.ReadFromJsonAsync<ImportStatusDto>(Json))!;
        return await WaitUntilSettledAsync(accepted.Id, timeout);
    }

    public async Task<ImportStatusDto> GetStatusAsync(Guid id) =>
        (await _client.GetFromJsonAsync<ImportStatusDto>($"/api/imports/{id}", Json))!;

    public async Task<ImportReportDto> GetReportAsync(Guid id) =>
        (await _client.GetFromJsonAsync<ImportReportDto>($"/api/imports/{id}/report", Json))!;

    public async Task<ImportRowsPageDto> GetRowsAsync(Guid id, string query = "") =>
        (await _client.GetFromJsonAsync<ImportRowsPageDto>($"/api/imports/{id}/rows{query}", Json))!;

    public async Task<ImportStatusDto> WaitUntilSettledAsync(Guid id, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(60));
        while (true)
        {
            var status = await GetStatusAsync(id);
            if (Settled.Contains(status.State))
            {
                return status;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Import {id} did not settle; last state {status.State}, staged {status.StagedRows}, error {status.Error}.");
            }

            await Task.Delay(200);
        }
    }

    private static HttpClient CreateClient(ApiFactory factory, string userId)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(XUserAuthenticationHandler.HeaderName, userId);
        return client;
    }
}

/// <summary>Locates the assignment's data files from the test output directory.</summary>
internal static class AssignmentData
{
    public static string PathOf(string fileName)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "AssignmentRequirements", fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"AssignmentRequirements/{fileName} not found above {AppContext.BaseDirectory}.");
    }
}
