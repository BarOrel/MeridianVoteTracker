using MeridianVoteTracker.Domain.Imports;
using MeridianVoteTracker.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using static MeridianVoteTracker.IntegrationTests.Infrastructure.TestData;

namespace MeridianVoteTracker.IntegrationTests.Persistence;

[Collection(ApiCollection.Name)]
public class UnitOfWorkTests(ApiFactory factory)
{
    private readonly TestData _data = new(factory);

    private static ImportJob NewJob() =>
        ImportJob.Create(Guid.NewGuid(), "uow.csv", "key", new string('b', 64), 1, "csv", "dana", Now, null);

    [Fact]
    public async Task Writes_outside_a_unit_of_work_are_rejected()
    {
        await using var scope = _data.OpenScope();
        scope.Db.ImportJobs.Add(NewJob());

        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Db.SaveChangesAsync());
    }

    [Fact]
    public async Task Nested_transactions_are_rejected()
    {
        await using var scope = _data.OpenScope();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.UnitOfWork.ExecuteInTransactionAsync(ct =>
                scope.UnitOfWork.ExecuteInTransactionAsync(_ => Task.CompletedTask, ct), CancellationToken.None));
    }

    [Fact]
    public async Task Failure_inside_the_boundary_rolls_back_everything_including_flushed_changes()
    {
        var job = NewJob();
        await using (var scope = _data.OpenScope())
        {
            await Assert.ThrowsAsync<TimeoutException>(() =>
                scope.UnitOfWork.ExecuteInTransactionAsync(async ct =>
                {
                    scope.Db.ImportJobs.Add(job);
                    await scope.Db.SaveChangesAsync(ct); // flushed, not committed
                    throw new TimeoutException("simulated failure after flush");
                }, CancellationToken.None));

            Assert.Empty(scope.Db.ChangeTracker.Entries());
        }

        await using var check = _data.OpenScope();
        Assert.False(await check.Db.ImportJobs.AnyAsync(j => j.Id == job.Id));
    }
}
