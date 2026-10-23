using MeridianVoteTracker.Application.Abstractions;

namespace MeridianVoteTracker.Infrastructure.Persistence;

/// <summary>
/// Explicit transaction boundary over the scoped <see cref="AppDbContext"/>: begin, run the use case's
/// work, flush pending changes, commit. On any exception the transaction is rolled back (by disposal)
/// and the change tracker is cleared so no half-applied entity state leaks into a later operation.
/// Read Committed isolation; correctness under concurrency comes from conditional updates,
/// concurrency tokens and unique constraints, not from a stricter isolation level.
/// </summary>
internal sealed class EfUnitOfWork(AppDbContext db) : IUnitOfWork
{
    public async Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is not null)
        {
            throw new InvalidOperationException("A transaction is already active. Nested transaction boundaries are not supported.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var result = await work(cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch
        {
            db.ChangeTracker.Clear();
            throw;
        }
    }

    public Task ExecuteInTransactionAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken) =>
        ExecuteInTransactionAsync<object?>(async ct =>
        {
            await work(ct);
            return null;
        }, cancellationToken);
}
