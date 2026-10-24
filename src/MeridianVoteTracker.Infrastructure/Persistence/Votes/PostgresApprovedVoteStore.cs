using MeridianVoteTracker.Application.Votes.Ports;
using MeridianVoteTracker.Domain.History;
using MeridianVoteTracker.Domain.Imports;
using MeridianVoteTracker.Domain.Votes;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace MeridianVoteTracker.Infrastructure.Persistence.Votes;

/// <summary>
/// Restricted writer of approved data. Opens <see cref="AppDbContext.AllowRestrictedWrites"/> only around its
/// own writes and flushes before closing it; the caller's unit of work commits. New country ballots (up to
/// ~1M for the historic file) go through binary COPY; votes, conflicts and history events through EF.
/// </summary>
internal sealed class PostgresApprovedVoteStore(AppDbContext db) : IApprovedVoteStore
{
    /// <summary>Key space for vote advisory locks, so they cannot collide with other advisory locks.</summary>
    private const long VoteLockSeed = 7001;

    private const string CopyCountryVotes = """
        COPY country_votes (vote_id, state_code, ballot, revision,
            approval_origin, approval_source_import_job_id, approval_source_row_number,
            approval_conflict_id, approval_approved_by, approval_approved_at)
        FROM STDIN (FORMAT BINARY)
        """;

    public async Task LockVotesAsync(IReadOnlyCollection<AssemblyVoteId> voteIds, CancellationToken cancellationToken)
    {
        EnsureTransaction();
        var ids = voteIds.Select(v => v.Value).ToArray();

        // Locks are taken in one statement, in a fixed order (sorted ids), so two transactions locking
        // overlapping sets cannot deadlock. Released automatically at commit/rollback.
        await db.Database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_xact_lock(hashtextextended(id, @seed)) FROM (SELECT DISTINCT id FROM unnest(@ids) AS t(id) ORDER BY id) AS ordered",
            [new NpgsqlParameter("seed", VoteLockSeed), new NpgsqlParameter("ids", ids)],
            cancellationToken);
    }

    public async Task SaveImportResultsAsync(Guid importJobId, IReadOnlyList<VoteClassificationResult> results, DateTimeOffset at, CancellationToken cancellationToken)
    {
        EnsureTransaction();
        using (db.AllowRestrictedWrites())
        {
            // 1. New votes first: their identity values are needed by ballots, outcomes and events.
            var created = results
                .Where(r => r.NewVote is not null)
                .Select(r => (Result: r, Vote: Vote.CreateFromImport(r.AssemblyVoteId, r.NewVote!.Session, r.NewVote.VoteDate, r.NewVote.Title,
                    importJobId, r.NewVote.FirstRowNumber, at)))
                .ToList();
            db.Votes.AddRange(created.Select(c => c.Vote));
            await db.SaveChangesAsync(cancellationToken);

            var voteIdOf = new Dictionary<AssemblyVoteId, int>();
            foreach (var (result, vote) in created)
            {
                result.Outcome.LinkCreatedVote(vote.Id);
                voteIdOf[result.AssemblyVoteId] = vote.Id;
                db.VoteHistoryEvents.Add(VoteHistoryEvent.VoteCreated(vote.Id, importJobId, result.NewVote!.FirstRowNumber, at));
            }

            foreach (var result in results.Where(r => r.ExistingVoteId is not null))
            {
                voteIdOf[result.AssemblyVoteId] = result.ExistingVoteId!.Value;
            }

            // 2. New ballots (initial-import provenance), one summary history event per vote.
            var ballots = results
                .SelectMany(r => r.NewBallots.Select(b => CountryVote.CreateFromImport(voteIdOf[r.AssemblyVoteId], b.Country, b.Ballot, importJobId, b.RowNumber, at)))
                .ToList();
            await CopyCountryVotesAsync(ballots, cancellationToken);
            foreach (var result in results.Where(r => r.NewBallots.Count > 0))
            {
                db.VoteHistoryEvents.Add(VoteHistoryEvent.CountryVotesAdded(voteIdOf[result.AssemblyVoteId], importJobId, result.NewBallots.Count, at));
            }

            // 3. New conflicts, then their "opened" events (the events reference the conflicts).
            var conflicts = results.SelectMany(r => r.NewConflicts).ToList();
            db.Conflicts.AddRange(conflicts);
            await db.SaveChangesAsync(cancellationToken);
            db.VoteHistoryEvents.AddRange(conflicts.Select(VoteHistoryEvent.ConflictOpened));
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<bool> ResolveConflictAsync(Guid conflictId, bool accept, string actor, string? comment, DateTimeOffset at, CancellationToken cancellationToken)
    {
        EnsureTransaction();
        var target = await db.Conflicts.AsNoTracking()
            .Where(c => c.Id == conflictId)
            .Join(db.Votes, c => c.VoteId, v => v.Id, (c, v) => new { v.Id, v.AssemblyVoteId })
            .SingleOrDefaultAsync(cancellationToken);
        if (target is null)
        {
            return false;
        }

        // Same per-vote lock as imports: decisions and classifications of one vote never interleave,
        // and everything below reads the state committed before the lock was granted.
        await LockVotesAsync([target.AssemblyVoteId], cancellationToken);

        using (db.AllowRestrictedWrites())
        {
            var conflict = await db.Conflicts.SingleAsync(c => c.Id == conflictId, cancellationToken);
            var events = new List<VoteHistoryEvent>();
            if (!accept)
            {
                conflict.Reject(actor, at, comment);
                events.Add(VoteHistoryEvent.ConflictRejected(conflict));
            }
            else
            {
                conflict.Accept(actor, at, comment);
                if (conflict.Kind == Domain.Conflicts.ConflictKind.CountryBallot)
                {
                    var approved = await db.CountryVotes.SingleAsync(cv => cv.VoteId == conflict.VoteId && cv.StateCode == conflict.StateCode, cancellationToken);
                    events.Add(approved.ApplyAcceptedConflict(conflict, at));
                }
                else
                {
                    var vote = await db.Votes.SingleAsync(v => v.Id == conflict.VoteId, cancellationToken);
                    events.Add(vote.ApplyAcceptedConflict(conflict, at));
                }

                // Other pending proposals for the same fact were made against a value that no longer exists.
                var siblings = await db.Conflicts
                    .Where(c => c.Id != conflictId && c.VoteId == conflict.VoteId && c.Status == Domain.Conflicts.ConflictStatus.Pending
                        && c.Kind == conflict.Kind && c.Field == conflict.Field && c.StateCode == conflict.StateCode)
                    .ToListAsync(cancellationToken);
                foreach (var sibling in siblings)
                {
                    sibling.Supersede(conflict.Id, at);
                    events.Add(VoteHistoryEvent.ConflictSuperseded(sibling));
                }
            }

            db.VoteHistoryEvents.AddRange(events);
            await db.SaveChangesAsync(cancellationToken);
        }

        return true;
    }

    public async Task<bool> SetPositionAsync(int voteId, Position position, Weight weight, int expectedRevision, string actor, DateTimeOffset at, CancellationToken cancellationToken)
    {
        EnsureTransaction();
        using (db.AllowRestrictedWrites())
        {
            var vote = await db.Votes.SingleOrDefaultAsync(v => v.Id == voteId, cancellationToken);
            if (vote is null)
            {
                return false;
            }

            db.VoteHistoryEvents.Add(vote.SetPosition(position, weight, expectedRevision, actor, at));
            await db.SaveChangesAsync(cancellationToken);
        }

        return true;
    }

    private async Task CopyCountryVotesAsync(IReadOnlyList<CountryVote> ballots, CancellationToken cancellationToken)
    {
        if (ballots.Count == 0)
        {
            return;
        }

        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await using var writer = await connection.BeginBinaryImportAsync(CopyCountryVotes, cancellationToken);
        foreach (var ballot in ballots)
        {
            var provenance = ballot.Provenance;
            await writer.StartRowAsync(cancellationToken);
            await writer.WriteAsync(ballot.VoteId, NpgsqlDbType.Integer, cancellationToken);
            await writer.WriteAsync(ballot.StateCode.Value, NpgsqlDbType.Char, cancellationToken);
            await writer.WriteAsync(ballot.Ballot.ToCode(), NpgsqlDbType.Char, cancellationToken);
            await writer.WriteAsync(ballot.Revision, NpgsqlDbType.Integer, cancellationToken);
            await writer.WriteAsync(provenance.Origin.ToString(), NpgsqlDbType.Varchar, cancellationToken);
            await writer.WriteAsync(provenance.SourceImportJobId, NpgsqlDbType.Uuid, cancellationToken);
            if (provenance.SourceRowNumber is { } row)
            {
                await writer.WriteAsync(row, NpgsqlDbType.Integer, cancellationToken);
            }
            else
            {
                await writer.WriteNullAsync(cancellationToken);
            }

            await writer.WriteNullAsync(cancellationToken); // conflict id: initial import has none
            await writer.WriteAsync(provenance.ApprovedBy, NpgsqlDbType.Varchar, cancellationToken);
            await writer.WriteAsync(provenance.ApprovedAt.ToUniversalTime(), NpgsqlDbType.TimestampTz, cancellationToken);
        }

        await writer.CompleteAsync(cancellationToken);
    }

    private void EnsureTransaction()
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Approved-data writes must run inside IUnitOfWork.ExecuteInTransactionAsync.");
        }
    }
}
