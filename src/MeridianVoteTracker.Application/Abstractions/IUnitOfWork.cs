namespace MeridianVoteTracker.Application.Abstractions;

/// <summary>
/// The only way a use case opens a database transaction. A use case decides the boundary
/// (what must be atomic); Infrastructure guarantees atomicity.
/// <list type="bullet">
/// <item>All changes made inside <paramref name="work"/> are committed once, at the end, or not at all.</item>
/// <item>No nesting: calling it while a transaction is active throws, so a boundary is never silently widened or split.</item>
/// <item>No implicit commits: persistence code may flush inside the transaction but cannot write outside one.</item>
/// </list>
/// </summary>
public interface IUnitOfWork
{
    Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken);

    Task ExecuteInTransactionAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken);
}
