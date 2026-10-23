using MeridianVoteTracker.Application.Abstractions;
using MeridianVoteTracker.Domain.Conflicts;
using MeridianVoteTracker.Domain.Imports;
using MeridianVoteTracker.Domain.Votes;
using MeridianVoteTracker.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static MeridianVoteTracker.IntegrationTests.Infrastructure.TestData;

namespace MeridianVoteTracker.IntegrationTests.Persistence;

/// <summary>Ordinary use-case code cannot change approved data, conflicts or the audit trail.</summary>
[Collection(ApiCollection.Name)]
public class RestrictedWriteGuardTests(ApiFactory factory)
{
    private readonly TestData _data = new(factory);

    [Fact]
    public async Task Use_case_context_and_unit_of_work_share_one_instance()
    {
        await using var scope = _data.OpenScope();

        Assert.Same(scope.Db, scope.Services.GetRequiredService<IApplicationDbContext>());
    }

    [Fact]
    public async Task Approved_data_read_through_the_use_case_context_is_not_tracked()
    {
        var (_, vote) = await _data.CreateVoteWithBrazilYesAsync();
        await using var scope = _data.OpenScope();
        var db = scope.Services.GetRequiredService<IApplicationDbContext>();

        await db.Votes.SingleAsync(v => v.Id == vote.Id);
        await db.CountryVotes.SingleAsync(cv => cv.VoteId == vote.Id);

        Assert.Empty(scope.Db.ChangeTracker.Entries());
    }

    [Fact]
    public async Task Ordinary_write_of_approved_data_is_rejected_and_nothing_is_persisted()
    {
        var (job, vote) = await _data.CreateVoteWithBrazilYesAsync();
        await using var scope = _data.OpenScope();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.UnitOfWork.ExecuteInTransactionAsync(async ct =>
            {
                var approved = await scope.Db.CountryVotes.SingleAsync(cv => cv.VoteId == vote.Id && cv.StateCode == Bra, ct);
                scope.Db.Conflicts.Add(Conflict.ForCountryBallot(approved, Ballot.No, job.Id, 9, Now));
            }, CancellationToken.None));

        Assert.Contains("Conflict", ex.Message);
        await using var check = _data.OpenScope();
        Assert.False(await check.Db.Conflicts.AnyAsync(c => c.VoteId == vote.Id));
    }

    [Fact]
    public async Task Restricted_change_left_pending_after_the_scope_closes_is_rejected()
    {
        var (job, vote) = await _data.CreateVoteWithBrazilYesAsync();
        await using var scope = _data.OpenScope();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.UnitOfWork.ExecuteInTransactionAsync(async ct =>
            {
                using (scope.Db.AllowRestrictedWrites())
                {
                    var approved = await scope.Db.CountryVotes.SingleAsync(cv => cv.VoteId == vote.Id && cv.StateCode == Bra, ct);
                    scope.Db.Conflicts.Add(Conflict.ForCountryBallot(approved, Ballot.No, job.Id, 9, Now));
                } // not flushed inside the scope
            }, CancellationToken.None));
    }

    [Fact]
    public async Task Workflow_entities_are_writable_through_the_use_case_context()
    {
        var job = ImportJob.Create(Guid.NewGuid(), "ordinary.csv", "key", new string('c', 64), 1, "csv", "dana", Now, null);
        await using (var scope = _data.OpenScope())
        {
            var db = scope.Services.GetRequiredService<IApplicationDbContext>();
            await scope.UnitOfWork.ExecuteInTransactionAsync(_ =>
            {
                db.ImportJobs.Add(job);
                return Task.CompletedTask;
            }, CancellationToken.None);
        }

        await using var check = _data.OpenScope();
        Assert.True(await check.Db.ImportJobs.AnyAsync(j => j.Id == job.Id));
    }
}
