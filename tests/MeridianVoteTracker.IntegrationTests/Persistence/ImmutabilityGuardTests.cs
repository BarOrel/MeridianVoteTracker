using MeridianVoteTracker.Domain.Imports;
using MeridianVoteTracker.Domain.MemberStates;
using MeridianVoteTracker.Domain.Votes;
using MeridianVoteTracker.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using static MeridianVoteTracker.IntegrationTests.Infrastructure.TestData;

namespace MeridianVoteTracker.IntegrationTests.Persistence;

/// <summary>Database triggers hold even for raw SQL that bypasses the domain model.</summary>
[Collection(ApiCollection.Name)]
public class ImmutabilityGuardTests(ApiFactory factory)
{
    private const string GuardViolation = "MV001";
    private readonly TestData _data = new(factory);

    [Fact]
    public async Task Vote_history_is_append_only()
    {
        var (_, vote) = await _data.CreateVoteWithBrazilYesAsync();

        await AssertRejected("UPDATE vote_history_events SET actor = 'mallory' WHERE vote_id = $1", vote.Id);
        await AssertRejected("DELETE FROM vote_history_events WHERE vote_id = $1", vote.Id);
        await AssertRejected("TRUNCATE vote_history_events CASCADE");
    }

    [Fact]
    public async Task Approved_data_is_never_deleted_and_changes_must_bump_the_revision()
    {
        var (_, vote) = await _data.CreateVoteWithBrazilYesAsync();

        await AssertRejected("DELETE FROM country_votes WHERE vote_id = $1", vote.Id);
        await AssertRejected("DELETE FROM votes WHERE id = $1", vote.Id);
        await AssertRejected("UPDATE country_votes SET ballot = 'N' WHERE vote_id = $1", vote.Id);
        await AssertRejected("UPDATE votes SET title = 'silently changed' WHERE id = $1", vote.Id);
        await AssertRejected("UPDATE votes SET assembly_vote_id = 'MA/1999/1' WHERE id = $1", vote.Id);
    }

    [Fact]
    public async Task Import_rows_are_writable_while_processing_and_frozen_once_finalized()
    {
        var job = await _data.CreateImportJobAsync();
        await AddStagedRowAsync(job.Id, 2);

        // Crash recovery during staging deletes and restages the job's rows: allowed while processing.
        Assert.Equal(1, await _data.ExecuteSqlAsync("DELETE FROM import_rows WHERE import_job_id = $1", job.Id));
        await AddStagedRowAsync(job.Id, 2);

        await using (var scope = _data.OpenScope())
        {
            await scope.UnitOfWork.ExecuteInTransactionAsync(async ct =>
            {
                var tracked = await scope.Db.ImportJobs.SingleAsync(j => j.Id == job.Id, ct);
                var row = await scope.Db.ImportRows.SingleAsync(r => r.ImportJobId == job.Id, ct);
                tracked.BeginAttempt("worker-1", Now, TimeSpan.FromMinutes(5));
                tracked.CompleteStaging(1);
                row.Classify(ImportClassification.New);

                // Row writes must reach the database before the job is finalized: EF does not
                // guarantee statement order within one SaveChanges, and the trigger freezes rows
                // as soon as the job row says "Completed".
                await scope.Db.SaveChangesAsync(ct);
                tracked.Complete(hasIssues: false, Now);
            }, CancellationToken.None);
        }

        await AssertRejected("UPDATE import_rows SET message = 'edited' WHERE import_job_id = $1", job.Id);
        await AssertRejected("DELETE FROM import_rows WHERE import_job_id = $1", job.Id);
        await AssertRejected("UPDATE import_jobs SET status = 'Queued' WHERE id = $1", job.Id);
        await AssertRejected("DELETE FROM import_jobs WHERE id = $1", job.Id);
        await Assert.ThrowsAsync<DbUpdateException>(() => AddStagedRowAsync(job.Id, 3));
    }

    [Fact]
    public async Task Raw_input_and_normalization_notes_round_trip_unchanged()
    {
        var job = await _data.CreateImportJobAsync();
        await AddStagedRowAsync(job.Id, 2594, rawCountryCode: "deu", notes: ["country_code: 'deu' → 'DEU' (uppercased)"]);

        await using var scope = _data.OpenScope();
        var row = await scope.Db.ImportRows.AsNoTracking().SingleAsync(r => r.ImportJobId == job.Id);

        Assert.Equal("deu", row.RawCountryCode);
        Assert.Equal(CountryCode.Create("DEU"), row.CountryCode);
        Assert.Equal(["country_code: 'deu' → 'DEU' (uppercased)"], row.NormalizationNotes);
        Assert.Null(row.Classification);
    }

    private async Task AddStagedRowAsync(Guid jobId, int rowNumber, string rawCountryCode = "DEU", string[]? notes = null)
    {
        var raw = new ImportRawFields("MA/2024/018", "79", "2024-02-10", "Indigenous Languages", rawCountryCode, "Germany", "X", "2024-04-02T09:00:00Z");
        var normalized = new NormalizedImportRecord(AssemblyVoteId.Create("MA/2024/018"), 79, new DateOnly(2024, 2, 10), "Indigenous Languages",
            CountryCode.Create("DEU"), "Germany", Ballot.Absent, new DateTimeOffset(2024, 4, 2, 9, 0, 0, TimeSpan.Zero));

        await using var scope = _data.OpenScope();
        await scope.UnitOfWork.ExecuteInTransactionAsync(_ =>
        {
            scope.Db.ImportRows.Add(ImportRow.Staged(jobId, rowNumber, "original line", raw, normalized, notes ?? []));
            return Task.CompletedTask;
        }, CancellationToken.None);
    }

    private async Task AssertRejected(string sql, params object[] parameters)
    {
        var ex = await Assert.ThrowsAsync<PostgresException>(() => _data.ExecuteSqlAsync(sql, parameters));
        Assert.Equal(GuardViolation, ex.SqlState);
    }
}
