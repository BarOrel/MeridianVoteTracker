using MeridianVoteTracker.Application.Abstractions;
using MeridianVoteTracker.Domain.History;
using MeridianVoteTracker.Domain.Imports;
using MeridianVoteTracker.Domain.MemberStates;
using MeridianVoteTracker.Domain.Votes;
using MeridianVoteTracker.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace MeridianVoteTracker.IntegrationTests.Infrastructure;

/// <summary>
/// Creates isolated test data through the real domain + unit of work. Approved data and audit rows
/// cannot be deleted (by design), so tests never clean up; they use unique vote ids instead.
/// </summary>
internal sealed class TestData(ApiFactory factory)
{
    private static int _voteSequence = Random.Shared.Next(1, 900_000);

    public static readonly CountryCode Bra = CountryCode.Create("BRA");

    public static DateTimeOffset Now => DateTimeOffset.UtcNow;

    public Scope OpenScope() => new(factory.Services.CreateAsyncScope());

    public NpgsqlDataSource DataSource => factory.Services.GetRequiredService<NpgsqlDataSource>();

    public async Task<ImportJob> CreateImportJobAsync()
    {
        var job = ImportJob.Create(Guid.NewGuid(), "test.csv", $"imports/{Guid.NewGuid()}", new string('a', 64), 10, "csv", "dana", Now, "test");
        await using var scope = OpenScope();
        await scope.UnitOfWork.ExecuteInTransactionAsync(_ =>
        {
            scope.Db.ImportJobs.Add(job);
            return Task.CompletedTask;
        }, CancellationToken.None);
        return job;
    }

    /// <summary>An approved vote created by an import, with Brazil voting Yes (revision 1).</summary>
    public async Task<(ImportJob Job, Vote Vote)> CreateVoteWithBrazilYesAsync()
    {
        var job = await CreateImportJobAsync();
        var vote = Vote.CreateFromImport(UniqueVoteId(), 79, new DateOnly(2024, 1, 11), "Maritime Safety, Navigation and Trade", job.Id, 2, Now);

        await using var scope = OpenScope();
        await scope.InRestrictedTransactionAsync(async (db, ct) =>
        {
            db.Votes.Add(vote);
            await db.SaveChangesAsync(ct); // flush inside the transaction to get the identity value
            db.CountryVotes.Add(CountryVote.CreateFromImport(vote.Id, Bra, Ballot.Yes, job.Id, 2, Now));
            db.VoteHistoryEvents.Add(VoteHistoryEvent.VoteCreated(vote.Id, job.Id, 2, Now));
        });
        return (job, vote);
    }

    public static AssemblyVoteId UniqueVoteId() => AssemblyVoteId.Create($"MA/2099/{Interlocked.Increment(ref _voteSequence)}");

    public async Task<int> ExecuteSqlAsync(string sql, params object[] parameters)
    {
        await using var command = DataSource.CreateCommand(sql);
        foreach (var value in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        }

        return await command.ExecuteNonQueryAsync();
    }

    internal sealed class Scope(AsyncServiceScope scope) : IAsyncDisposable
    {
        public AppDbContext Db { get; } = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        public IUnitOfWork UnitOfWork { get; } = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        public IServiceProvider Services => scope.ServiceProvider;

        /// <summary>
        /// Writes approved data / conflicts / audit rows the way a dedicated persistence port does:
        /// inside a unit of work, within a restricted-write scope, flushed before the scope closes.
        /// </summary>
        public Task InRestrictedTransactionAsync(Func<AppDbContext, CancellationToken, Task> work) =>
            InRestrictedTransactionAsync<object?>(async (db, ct) =>
            {
                await work(db, ct);
                return null;
            });

        public Task<T> InRestrictedTransactionAsync<T>(Func<AppDbContext, CancellationToken, Task<T>> work) =>
            UnitOfWork.ExecuteInTransactionAsync(async ct =>
            {
                using var _ = Db.AllowRestrictedWrites();
                var result = await work(Db, ct);
                await Db.SaveChangesAsync(ct);
                return result;
            }, CancellationToken.None);

        public ValueTask DisposeAsync() => scope.DisposeAsync();
    }
}
