using MeridianVoteTracker.Application.Abstractions;
using MeridianVoteTracker.Application.Imports;
using MeridianVoteTracker.Application.Imports.GetImportStatus;
using MeridianVoteTracker.Application.Imports.Pipeline;
using MeridianVoteTracker.Application.Imports.Ports;
using MeridianVoteTracker.Application.Imports.ProcessImport;
using MeridianVoteTracker.Application.Votes.Ports;
using MeridianVoteTracker.Domain.Imports;
using MeridianVoteTracker.Domain.Votes;
using MeridianVoteTracker.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static MeridianVoteTracker.IntegrationTests.Infrastructure.TestData;

namespace MeridianVoteTracker.IntegrationTests.Imports;

/// <summary>Crash recovery and concurrency of the classification phase, against real PostgreSQL.</summary>
[Collection(ImportCollection.Name)]
public class ClassificationReliabilityTests(ImportApiFactory factory)
{
    private readonly ImportTestClient _dana = new(factory);
    private readonly TestData _data = new(factory);

    /// <summary>Lets the first N classification batches commit, then fails, simulating a crash mid-classification.</summary>
    private sealed class CrashingStagingStore(IImportStagingStore inner, int succeedingBatches) : IImportStagingStore
    {
        private int _calls;

        public Task ResetAsync(Guid importJobId, CancellationToken cancellationToken) => inner.ResetAsync(importJobId, cancellationToken);

        public Task WriteBatchAsync(IReadOnlyList<ImportRow> rows, CancellationToken cancellationToken) => inner.WriteBatchAsync(rows, cancellationToken);

        public Task SaveClassificationsAsync(Guid importJobId, IReadOnlyList<ImportRow> rows, CancellationToken cancellationToken) =>
            ++_calls > succeedingBatches
                ? throw new IOException("simulated crash during classification")
                : inner.SaveClassificationsAsync(importJobId, rows, cancellationToken);
    }

    [Fact]
    public async Task Crash_during_classification_resumes_without_touching_committed_batches()
    {
        // 150 votes × 5 states, 50 votes per batch → 3 classification batches; the attempt dies in the 3rd.
        var job = await CreateLeasedJobAsync(ImportProcessingTests.ValidCsv(rows: 750), "worker-a");
        await using (var scope = _data.OpenScope())
        {
            var crashing = CreateProcessUseCase(scope.Services, new CrashingStagingStore(scope.Services.GetRequiredService<IImportStagingStore>(), succeedingBatches: 2));
            await Assert.ThrowsAsync<IOException>(() => crashing.ExecuteAsync(new ImportClaim(job.Id, "worker-a", 1, null), CancellationToken.None));
        }

        var afterCrash = await ClassifiedRowsAsync(job.Id);
        Assert.Equal(500, afterCrash.Count); // 2 committed batches; the 3rd rolled back completely
        await using (var scope = _data.OpenScope())
        {
            Assert.Equal(100, await scope.Db.ImportVoteOutcomes.CountAsync(o => o.ImportJobId == job.Id));
            var staged = await scope.Db.ImportJobs.AsNoTracking().SingleAsync(j => j.Id == job.Id);
            Assert.Equal((ImportPhase.Classifying, 500), (staged.Phase, staged.ClassifiedRows));
        }

        // Worker B takes over after A's lease expired and finishes the remaining votes only.
        await TakeOverAsync(job.Id, "worker-b");
        await using (var scope = _data.OpenScope())
        {
            await scope.Services.GetRequiredService<ProcessImportUseCase>().ExecuteAsync(new ImportClaim(job.Id, "worker-b", 2, null), CancellationToken.None);
        }

        var final = await ClassifiedRowsAsync(job.Id);
        Assert.Equal(750, final.Count);
        Assert.All(afterCrash, row => Assert.Equal(row.Value, final[row.Key])); // committed classifications untouched
        Assert.All(final.Values, c => Assert.Equal(ImportClassification.New, c));

        var status = await _dana.GetStatusAsync(job.Id);
        Assert.Equal((ImportProgressState.Completed, 750, 2), (status.State, status.ClassifiedRows, status.Attempts));
        await using (var scope = _data.OpenScope())
        {
            Assert.Equal(150, await scope.Db.ImportVoteOutcomes.CountAsync(o => o.ImportJobId == job.Id));
            var voteIds = await scope.Db.ImportVoteOutcomes.Where(o => o.ImportJobId == job.Id).Select(o => o.VoteId).ToListAsync();
            Assert.Equal(750, await scope.Db.CountryVotes.CountAsync(cv => voteIds.Contains(cv.VoteId)));
        }
    }

    [Fact]
    public async Task Two_imports_of_the_same_data_classified_concurrently_store_each_value_once()
    {
        var file = ImportProcessingTests.ValidCsv(rows: 500);
        var first = await CreateLeasedJobAsync(file, "worker-1");
        var second = await CreateLeasedJobAsync(file, "worker-2");

        await Task.WhenAll(
            RunAsync(new ImportClaim(first.Id, "worker-1", 1, null)),
            RunAsync(new ImportClaim(second.Id, "worker-2", 1, null)));

        var a = await ClassifiedRowsAsync(first.Id);
        var b = await ClassifiedRowsAsync(second.Id);
        Assert.Equal(500, a.Values.Count(c => c == ImportClassification.New) + b.Values.Count(c => c == ImportClassification.New));
        Assert.Equal(500, a.Values.Count(c => c == ImportClassification.Unchanged) + b.Values.Count(c => c == ImportClassification.Unchanged));

        await using var scope = _data.OpenScope();
        var created = await scope.Db.ImportVoteOutcomes
            .Where(o => (o.ImportJobId == first.Id || o.ImportJobId == second.Id) && o.Outcome == VoteImportOutcome.NewVote)
            .Select(o => o.VoteId)
            .ToListAsync();
        Assert.Equal(100, created.Count); // each of the 100 votes created by exactly one of the two imports
        Assert.Equal(500, await scope.Db.CountryVotes.CountAsync(cv => created.Contains(cv.VoteId)));
    }

    [Fact]
    public async Task Concurrent_imports_proposing_the_same_change_open_one_conflict()
    {
        var original = ImportProcessingTests.ValidCsv(rows: 50);
        var initial = await CreateLeasedJobAsync(original, "worker-0");
        await RunAsync(new ImportClaim(initial.Id, "worker-0", 1, null));

        // The same 10 votes, now with every ballot changed to N, imported twice at the same time.
        var corrected = System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.UTF8.GetString(original).Replace(",Y,", ",N,"));
        var x = await CreateLeasedJobAsync(corrected, "worker-x");
        var y = await CreateLeasedJobAsync(corrected, "worker-y");
        await Task.WhenAll(RunAsync(new ImportClaim(x.Id, "worker-x", 1, null)), RunAsync(new ImportClaim(y.Id, "worker-y", 1, null)));

        await using var scope = _data.OpenScope();
        var conflicts = await scope.Db.Conflicts.CountAsync(c => c.SourceImportJobId == x.Id || c.SourceImportJobId == y.Id);
        Assert.Equal(50, conflicts);
        var linked = await scope.Db.ImportRows.CountAsync(r =>
            (r.ImportJobId == x.Id || r.ImportJobId == y.Id) && r.ReasonCode == "conflict.already_pending");
        Assert.Equal(50, linked);
    }

    private async Task RunAsync(ImportClaim claim)
    {
        await using var scope = _data.OpenScope();
        await scope.Services.GetRequiredService<ProcessImportUseCase>().ExecuteAsync(claim, CancellationToken.None);
    }

    /// <summary>Stored file + job leased to a test worker for 30 minutes, so the background worker stays away.</summary>
    private async Task<ImportJob> CreateLeasedJobAsync(byte[] content, string workerId)
    {
        await using var scope = _data.OpenScope();
        var id = Guid.NewGuid();
        var stored = await scope.Services.GetRequiredService<IImportFileStorage>().SaveAsync(id, "test.csv", new MemoryStream(content), CancellationToken.None);
        var job = ImportJob.Create(id, "test.csv", stored.StorageKey, stored.Sha256, stored.SizeBytes, "csv", "dana", Now, "test");
        await scope.UnitOfWork.ExecuteInTransactionAsync(_ =>
        {
            job.BeginAttempt(workerId, Now, TimeSpan.FromMinutes(30));
            scope.Db.ImportJobs.Add(job);
            return Task.CompletedTask;
        }, CancellationToken.None);
        return job;
    }

    private async Task TakeOverAsync(Guid jobId, string workerId)
    {
        await using var scope = _data.OpenScope();
        await scope.UnitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var job = await scope.Db.ImportJobs.SingleAsync(j => j.Id == jobId, ct);
            job.BeginAttempt(workerId, Now + TimeSpan.FromMinutes(31), TimeSpan.FromMinutes(30)); // after A's lease
        }, CancellationToken.None);
    }

    private async Task<Dictionary<int, ImportClassification>> ClassifiedRowsAsync(Guid jobId)
    {
        await using var scope = _data.OpenScope();
        return await scope.Db.ImportRows.AsNoTracking()
            .Where(r => r.ImportJobId == jobId && r.Classification != null)
            .ToDictionaryAsync(r => r.RowNumber, r => r.Classification!.Value);
    }

    private static ProcessImportUseCase CreateProcessUseCase(IServiceProvider services, IImportStagingStore staging) => new(
        services.GetRequiredService<IApplicationDbContext>(),
        services.GetRequiredService<IUnitOfWork>(),
        services.GetRequiredService<IImportFileStorage>(),
        services.GetRequiredService<ImportFileReaderResolver>(),
        staging,
        services.GetRequiredService<IApprovedVoteStore>(),
        services.GetRequiredService<TimeProvider>(),
        services.GetRequiredService<ImportProcessingSettings>());
}
