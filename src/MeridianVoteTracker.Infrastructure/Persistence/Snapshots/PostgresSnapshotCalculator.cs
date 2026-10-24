using MeridianVoteTracker.Application.Snapshots.Ports;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MeridianVoteTracker.Infrastructure.Persistence.Snapshots;

/// <summary>
/// Computes a snapshot with a single SQL statement (data-modifying CTEs): copy the selected votes'
/// position/weight, copy the approved ballots, aggregate per member state. One statement sees one consistent
/// state of approved data even if a conflict is resolved at the same moment.
/// Rules (see Snapshot.Rules): only Y/N counted; match = Y with "For" or N with "Against"; no counted vote → no score.
/// A ballot counts only if the state was a member on the vote's approved date. Imports already reject such
/// ballots, but an accepted vote-date correction can move a vote outside a state's membership; that ballot is
/// still copied (inputs stay complete and approved data is untouched) but marked not counted.
/// </summary>
internal sealed class PostgresSnapshotCalculator(AppDbContext db) : ISnapshotCalculator
{
    private const string Calculate = """
        WITH sv AS (
            INSERT INTO snapshot_votes (snapshot_id, vote_id, assembly_vote_id, title, vote_date, position, weight)
            SELECT @id, v.id, v.assembly_vote_id, v.title, v.vote_date, v.position, v.weight
            FROM votes v WHERE v.id = ANY(@votes)
            RETURNING vote_id, vote_date, position, weight
        ), ballots AS (
            SELECT cv.vote_id, cv.state_code, cv.ballot, sv.position, sv.weight,
                   EXISTS (SELECT 1 FROM membership_periods mp
                           WHERE mp.state_code = cv.state_code
                             AND sv.vote_date >= mp.member_from
                             AND (mp.member_to IS NULL OR sv.vote_date <= mp.member_to)) AS was_member
            FROM country_votes cv JOIN sv ON sv.vote_id = cv.vote_id
        ), se AS (
            INSERT INTO snapshot_entries (snapshot_id, vote_id, state_code, ballot, counted, matching, weight)
            SELECT @id, vote_id, state_code, ballot,
                   was_member AND ballot IN ('Y', 'N'),
                   was_member AND ((ballot = 'Y' AND position = 'For') OR (ballot = 'N' AND position = 'Against')),
                   weight
            FROM ballots
            RETURNING state_code, counted, matching, weight
        ), totals AS (
            SELECT m.code AS state_code,
                   COALESCE(SUM(se.weight) FILTER (WHERE se.counted), 0)::int AS counted_weight,
                   COALESCE(SUM(se.weight) FILTER (WHERE se.matching), 0)::int AS matching_weight
            FROM member_states m LEFT JOIN se ON se.state_code = m.code
            GROUP BY m.code
        ), scored AS (
            SELECT state_code, counted_weight, matching_weight,
                   CASE WHEN counted_weight > 0 THEN ROUND(100.0 * matching_weight / counted_weight, 1) END AS score
            FROM totals
        )
        INSERT INTO snapshot_results (snapshot_id, state_code, counted_weight, matching_weight, score, rank)
        SELECT @id, state_code, counted_weight, matching_weight, score,
               CASE WHEN score IS NOT NULL THEN RANK() OVER (PARTITION BY score IS NULL ORDER BY score DESC) END
        FROM scored
        """;

    public async Task<Guid?> LockNextPendingAsync(CancellationToken cancellationToken)
    {
        EnsureTransaction();
        var ids = await db.Database
            .SqlQueryRaw<Guid>("SELECT id AS \"Value\" FROM snapshots WHERE status = 'Pending' ORDER BY created_at LIMIT 1 FOR UPDATE SKIP LOCKED")
            .ToListAsync(cancellationToken);
        return ids.Count == 0 ? null : ids[0];
    }

    public async Task CalculateAsync(Guid snapshotId, IReadOnlyList<int> voteIds, CancellationToken cancellationToken)
    {
        EnsureTransaction();

        // The full history (~1M ballots) takes ~40 s in Docker, beyond the 30 s default. Raise the timeout for
        // this one statement only and restore it, so every other command keeps the default.
        var previousTimeout = db.Database.GetCommandTimeout();
        db.Database.SetCommandTimeout(CalculationTimeout);
        try
        {
            await db.Database.ExecuteSqlRawAsync(Calculate,
                [new NpgsqlParameter("id", snapshotId), new NpgsqlParameter("votes", voteIds.ToArray())], cancellationToken);
        }
        finally
        {
            db.Database.SetCommandTimeout(previousTimeout);
        }
    }

    internal static readonly TimeSpan CalculationTimeout = TimeSpan.FromMinutes(10);

    private void EnsureTransaction()
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Snapshot calculation must run inside IUnitOfWork.ExecuteInTransactionAsync.");
        }
    }
}
