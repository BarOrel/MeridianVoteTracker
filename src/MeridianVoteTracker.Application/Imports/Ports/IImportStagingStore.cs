using MeridianVoteTracker.Domain.Imports;

namespace MeridianVoteTracker.Application.Imports.Ports;

/// <summary>
/// Bulk writes of staged rows. Exists because ~1M rows must be written fast and with bounded memory:
/// PostgreSQL binary COPY instead of EF change tracking (rows are never tracked).
/// Both methods require an open transaction (they join the caller's <see cref="Abstractions.IUnitOfWork"/>
/// boundary), so a batch and the job's progress update commit together.
/// </summary>
public interface IImportStagingStore
{
    /// <summary>Deletes the job's staged rows: staging restarts from the file after a crash.</summary>
    Task ResetAsync(Guid importJobId, CancellationToken cancellationToken);

    Task WriteBatchAsync(IReadOnlyList<ImportRow> rows, CancellationToken cancellationToken);
}
