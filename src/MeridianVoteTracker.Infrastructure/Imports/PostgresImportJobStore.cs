using MeridianVoteTracker.Application.Imports.Ports;
using MeridianVoteTracker.Domain.Imports;
using MeridianVoteTracker.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MeridianVoteTracker.Infrastructure.Imports;

/// <summary>
/// Claims work with FOR UPDATE SKIP LOCKED: a job locked by one worker's open transaction is skipped by
/// the others instead of blocking them. Claimable: Queued, or Processing with an expired lease (crashed
/// worker, or retry time reached). A parked job (no lease at all) is not claimable.
/// </summary>
internal sealed class PostgresImportJobStore(AppDbContext db) : IImportJobStore
{
    public async Task<ImportJob?> LockNextClaimableAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Claiming must run inside IUnitOfWork.ExecuteInTransactionAsync; the row lock lasts until commit.");
        }

        var candidates = await db.ImportJobs
            .FromSql($"""
                SELECT * FROM import_jobs
                WHERE status = 'Queued' OR (status = 'Processing' AND lease_expires_at < {now})
                ORDER BY uploaded_at
                LIMIT 1
                FOR UPDATE SKIP LOCKED
                """)
            .AsTracking()
            .ToListAsync(cancellationToken);

        return candidates.SingleOrDefault();
    }
}
