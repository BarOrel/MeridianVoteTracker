using System.Text;
using MeridianVoteTracker.Application.Imports;
using MeridianVoteTracker.Application.Imports.GetImportStatus;
using MeridianVoteTracker.Application.Imports.Pipeline;
using MeridianVoteTracker.Application.Imports.Ports;
using MeridianVoteTracker.Application.Imports.ProcessImport;
using MeridianVoteTracker.Domain.Imports;
using MeridianVoteTracker.Domain.MemberStates;
using MeridianVoteTracker.Domain.Votes;
using MeridianVoteTracker.Infrastructure.Persistence;
using MeridianVoteTracker.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static MeridianVoteTracker.IntegrationTests.Infrastructure.TestData;

namespace MeridianVoteTracker.IntegrationTests.Imports;

[Collection(ImportCollection.Name)]
public class ImportProcessingTests(ImportApiFactory factory)
{
    private const string Header = "assembly_vote_id,session,vote_date,resolution_title,country_code,country_name,ballot,published_at";

    private readonly ImportTestClient _dana = new(factory);
    private readonly TestData _data = new(factory);

    /// <summary>
    /// Physical lines of assembly_historic.csv with known problems (found by profiling the file), and the
    /// reason each must be rejected with. Rows of one vote are not contiguous in this file (ING-03).
    /// </summary>
    private static readonly Dictionary<int, string> HistoricProblemLines = new()
    {
        [134746] = ImportReasonCodes.ColumnCount,       // 7 fields
        [235011] = ImportReasonCodes.BallotInvalid,     // "0"
        [319853] = ImportReasonCodes.BallotInvalid,     // "YES"
        [340903] = ImportReasonCodes.BallotInvalid,     // "?"
        [487988] = ImportReasonCodes.CountryCodeUnknown, // "XXX"
        [521760] = ImportReasonCodes.Required,          // unquoted comma in title shifts columns: ballot empty, code garbage
        [328072] = ImportReasonCodes.NotMemberOnDate,   // TLS in 2022 (member from 2023)
        [905768] = ImportReasonCodes.NotMemberOnDate,   // SSD in 2015 (member from 2016-03)
    };

    [Fact]
    public async Task Representative_sample_of_the_historic_export_is_staged_with_bounded_batches()
    {
        // Every 50th data line (~19k rows, so ~20 batches of 1,000) plus all known problem lines.
        var sample = new StringBuilder(Header).Append('\n');
        var problemTexts = new Dictionary<string, string>();
        var sampled = 0;
        var lineNumber = 0;
        foreach (var line in File.ReadLines(AssignmentData.PathOf("assembly_historic.csv")))
        {
            lineNumber++;
            var isProblem = HistoricProblemLines.TryGetValue(lineNumber, out var reason);
            if (lineNumber > 1 && (lineNumber % 50 == 0 || isProblem))
            {
                sample.Append(line).Append('\n');
                sampled++;
                if (isProblem)
                {
                    problemTexts[line] = reason!;
                }
            }
        }

        var status = await _dana.UploadAndWaitAsync("historic_sample.csv", Encoding.UTF8.GetBytes(sample.ToString()), TimeSpan.FromMinutes(2));

        Assert.Equal(ImportProgressState.AwaitingClassification, status.State);
        Assert.Equal(sampled, status.TotalRows);
        Assert.Equal(HistoricProblemLines.Count, problemTexts.Count);

        await using var scope = _data.OpenScope();
        var invalid = await scope.Db.ImportRows.AsNoTracking()
            .Where(r => r.ImportJobId == status.Id && r.Classification == ImportClassification.Invalid)
            .ToListAsync();
        foreach (var (text, reason) in problemTexts)
        {
            var row = Assert.Single(invalid, r => r.OriginalText == text);
            Assert.Equal(reason, row.ReasonCode);
        }

        Assert.Equal(sampled, await scope.Db.ImportRows.CountAsync(r => r.ImportJobId == status.Id));
        var validRows = await scope.Db.ImportRows.CountAsync(r => r.ImportJobId == status.Id && r.Classification == null);
        Assert.Equal(sampled - invalid.Count, validRows);
    }

    [Fact]
    public async Task Job_abandoned_by_a_crashed_worker_is_resumed_and_staging_restarts_cleanly()
    {
        // A worker claimed the job, staged part of it and died; its lease has expired.
        var crashedAt = Now - TimeSpan.FromMinutes(10);
        var job = await CreateStoredJobAsync(ValidCsv(rows: 2500), (job, db) =>
        {
            job.BeginAttempt("crashed-worker", crashedAt, TimeSpan.FromMinutes(1));
            job.RecordStagingProgress(1, crashedAt, TimeSpan.FromMinutes(1));
            db.ImportRows.Add(ImportRow.Invalid(job.Id, 2, "partial", new(null, null, null, null, null, null, null, null), "x", "left over", []));
        });

        var status = await _dana.WaitUntilSettledAsync(job.Id);

        Assert.Equal(ImportProgressState.AwaitingClassification, status.State);
        Assert.Equal(2, status.Attempts);
        Assert.Equal(2500, status.TotalRows);
        Assert.Equal(0, status.InvalidRows); // the crashed attempt's leftovers were removed
    }

    [Fact]
    public async Task Worker_whose_lease_was_taken_over_cannot_write_anything()
    {
        // Worker A claimed the job, stalled past its lease, and worker B took over (attempt 2).
        // B's long lease keeps the background worker away, so the test drives both attempts itself.
        var job = await CreateStoredJobAsync(ValidCsv(rows: 1500), (job, _) =>
        {
            job.BeginAttempt("worker-a", Now - TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(1));
            job.BeginAttempt("worker-b", Now, TimeSpan.FromMinutes(30));
        });

        await using (var scope = _data.OpenScope())
        {
            var process = scope.Services.GetRequiredService<ProcessImportUseCase>();
            await Assert.ThrowsAsync<ImportLeaseLostException>(() =>
                process.ExecuteAsync(new ImportClaim(job.Id, "worker-a", 1, null), CancellationToken.None));
        }

        Assert.Equal(0, await CountRowsAsync(job.Id));

        await using (var scope = _data.OpenScope())
        {
            await scope.Services.GetRequiredService<ProcessImportUseCase>()
                .ExecuteAsync(new ImportClaim(job.Id, "worker-b", 2, null), CancellationToken.None);
        }

        var status = await _dana.GetStatusAsync(job.Id);
        Assert.Equal((ImportProgressState.AwaitingClassification, 1500), (status.State, status.TotalRows!.Value));
        Assert.Equal(1500, await CountRowsAsync(job.Id));
    }

    [Fact]
    public async Task Transient_failure_schedules_a_retry_and_the_error_is_visible()
    {
        var job = await CreateStoredJobAsync(ValidCsv(rows: 10), (job, _) => job.BeginAttempt("worker-x", Now, TimeSpan.FromMinutes(30)));
        await using (var scope = _data.OpenScope())
        {
            await scope.Services.GetRequiredService<RecordImportFailureUseCase>()
                .ExecuteAsync(new ImportClaim(job.Id, "worker-x", 1, null), new TimeoutException("database timeout"), CancellationToken.None);
        }

        // Retry delay in tests is the default (15 s × attempt): the job waits, visibly, before being retried.
        var status = await _dana.GetStatusAsync(job.Id);
        Assert.Equal(ImportProgressState.RetryScheduled, status.State);
        Assert.Equal("Attempt 1 failed: database timeout", status.Error);
    }

    /// <summary>
    /// Stores a file and inserts its job already in the scenario's state, in one transaction, so the
    /// background worker never sees an intermediate state.
    /// </summary>
    private async Task<ImportJob> CreateStoredJobAsync(byte[] content, Action<ImportJob, AppDbContext> setUp)
    {
        await using var scope = _data.OpenScope();
        var id = Guid.NewGuid();
        var stored = await scope.Services.GetRequiredService<IImportFileStorage>().SaveAsync(id, "test.csv", new MemoryStream(content), CancellationToken.None);
        var job = ImportJob.Create(id, "test.csv", stored.StorageKey, stored.Sha256, stored.SizeBytes, "csv", "dana", Now - TimeSpan.FromHours(1), "test");

        await scope.UnitOfWork.ExecuteInTransactionAsync(_ =>
        {
            scope.Db.ImportJobs.Add(job);
            setUp(job, scope.Db);
            return Task.CompletedTask;
        }, CancellationToken.None);
        return job;
    }

    private async Task<int> CountRowsAsync(Guid jobId)
    {
        await using var scope = _data.OpenScope();
        return await scope.Db.ImportRows.CountAsync(r => r.ImportJobId == jobId);
    }

    private static byte[] ValidCsv(int rows)
    {
        var codes = new[] { "FRA", "BRA", "JPN", "DEU", "IND" };
        var csv = new StringBuilder(Header).Append('\n');
        for (var i = 0; i < rows; i++)
        {
            csv.Append($"MA/2099/{i / 5 + 1},79,2024-01-11,Test vote,{codes[i % 5]},Name,Y,2024-04-02T09:00:00Z\n");
        }

        return Encoding.UTF8.GetBytes(csv.ToString());
    }
}
