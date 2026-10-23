using MeridianVoteTracker.Domain.Conflicts;
using MeridianVoteTracker.Domain.History;
using MeridianVoteTracker.Domain.Votes;
using MeridianVoteTracker.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using static MeridianVoteTracker.IntegrationTests.Infrastructure.TestData;

namespace MeridianVoteTracker.IntegrationTests.Persistence;

/// <summary>
/// The approved value is updated conditionally (revision) in the same transaction as the conflict
/// (status), so concurrent decisions can never produce a lost update.
/// </summary>
[Collection(ApiCollection.Name)]
public class ApprovedDataConcurrencyTests(ApiFactory factory)
{
    private readonly TestData _data = new(factory);

    [Fact]
    public async Task Two_different_conflicts_on_the_same_country_vote_cannot_both_be_applied()
    {
        var (job, vote) = await _data.CreateVoteWithBrazilYesAsync();
        var (abstainId, noId) = await CreatePendingConflictsAsync(job.Id, vote.Id, Ballot.Abstain, Ballot.No);

        // Both analysts load the same approved state (revision 1) before either decides.
        await using var dana = _data.OpenScope();
        await using var omar = _data.OpenScope();
        var danaVote = await dana.Db.CountryVotes.SingleAsync(cv => cv.VoteId == vote.Id && cv.StateCode == Bra);
        var danaConflict = await dana.Db.Conflicts.SingleAsync(c => c.Id == abstainId);
        var omarVote = await omar.Db.CountryVotes.SingleAsync(cv => cv.VoteId == vote.Id && cv.StateCode == Bra);
        var omarConflict = await omar.Db.Conflicts.SingleAsync(c => c.Id == noId);

        await dana.InRestrictedTransactionAsync((db, _) =>
        {
            danaConflict.Accept("dana", Now, null);
            db.VoteHistoryEvents.Add(danaVote.ApplyAcceptedConflict(danaConflict, Now));
            return Task.CompletedTask;
        });

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
            omar.InRestrictedTransactionAsync((db, _) =>
            {
                omarConflict.Accept("omar", Now, null);
                db.VoteHistoryEvents.Add(omarVote.ApplyAcceptedConflict(omarConflict, Now));
                return Task.CompletedTask;
            }));

        await using var check = _data.OpenScope();
        var approved = await check.Db.CountryVotes.AsNoTracking().SingleAsync(cv => cv.VoteId == vote.Id && cv.StateCode == Bra);
        Assert.Equal((Ballot.Abstain, 2), (approved.Ballot, approved.Revision));
        Assert.Equal(ConflictStatus.Pending, (await check.Db.Conflicts.AsNoTracking().SingleAsync(c => c.Id == noId)).Status);
        Assert.Equal(1, await check.Db.VoteHistoryEvents.CountAsync(e => e.VoteId == vote.Id && e.Type == VoteHistoryEventType.ApprovedValueCorrected));
    }

    [Fact]
    public async Task Two_analysts_deciding_the_same_conflict_only_one_decision_is_stored()
    {
        var (job, vote) = await _data.CreateVoteWithBrazilYesAsync();
        var (conflictId, _) = await CreatePendingConflictsAsync(job.Id, vote.Id, Ballot.Abstain, Ballot.No);

        await using var dana = _data.OpenScope();
        await using var omar = _data.OpenScope();
        var danaVote = await dana.Db.CountryVotes.SingleAsync(cv => cv.VoteId == vote.Id && cv.StateCode == Bra);
        var danaView = await dana.Db.Conflicts.SingleAsync(c => c.Id == conflictId);
        var omarView = await omar.Db.Conflicts.SingleAsync(c => c.Id == conflictId);

        await dana.InRestrictedTransactionAsync((db, _) =>
        {
            danaView.Accept("dana", Now, "Corrigendum");
            db.VoteHistoryEvents.Add(danaVote.ApplyAcceptedConflict(danaView, Now));
            return Task.CompletedTask;
        });

        // Omar's stale copy still says Pending, so the domain allows Reject; the conditional UPDATE does not.
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
            omar.InRestrictedTransactionAsync((db, _) =>
            {
                omarView.Reject("omar", Now, null);
                db.VoteHistoryEvents.Add(VoteHistoryEvent.ConflictRejected(omarView));
                return Task.CompletedTask;
            }));

        await using var check = _data.OpenScope();
        var stored = await check.Db.Conflicts.AsNoTracking().SingleAsync(c => c.Id == conflictId);
        Assert.Equal((ConflictStatus.Accepted, "dana"), (stored.Status, stored.ResolvedBy));
    }

    [Fact]
    public async Task Database_allows_only_one_pending_conflict_per_fact_and_proposed_value()
    {
        var (job, vote) = await _data.CreateVoteWithBrazilYesAsync();
        await CreatePendingConflictsAsync(job.Id, vote.Id, Ballot.Abstain, Ballot.No);

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => CreatePendingConflictsAsync(job.Id, vote.Id, Ballot.Abstain, Ballot.Absent));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(ex.InnerException).SqlState);
    }

    private async Task<(Guid First, Guid Second)> CreatePendingConflictsAsync(Guid importJobId, int voteId, Ballot first, Ballot second)
    {
        await using var scope = _data.OpenScope();
        return await scope.InRestrictedTransactionAsync(async (db, ct) =>
        {
            var approved = await db.CountryVotes.SingleAsync(cv => cv.VoteId == voteId && cv.StateCode == Bra, ct);
            var a = Conflict.ForCountryBallot(approved, first, importJobId, 3, Now);
            var b = Conflict.ForCountryBallot(approved, second, importJobId, 4, Now);
            db.Conflicts.AddRange(a, b);
            return (a.Id, b.Id);
        });
    }
}
