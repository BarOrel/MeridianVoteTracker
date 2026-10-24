using System.Net;
using System.Net.Http.Json;
using System.Text;
using MeridianVoteTracker.Api.Auth;
using MeridianVoteTracker.Application.Conflicts;
using MeridianVoteTracker.Application.Snapshots;
using MeridianVoteTracker.Application.Votes;
using MeridianVoteTracker.Domain.Snapshots;
using MeridianVoteTracker.IntegrationTests.Infrastructure;
using Npgsql;

namespace MeridianVoteTracker.IntegrationTests.Snapshots;

/// <summary>SNP-01..05 with the brief's scoring example and the "published index never changes" scenario.</summary>
[Collection(ImportCollection.Name)]
public class SnapshotTests(ImportApiFactory factory)
{
    private const string Header = "assembly_vote_id,session,vote_date,resolution_title,country_code,country_name,ballot,published_at";
    private static int _year = 7000;

    private readonly ImportTestClient _importer = new(factory);
    private readonly HttpClient _dana = Client(factory, "dana");
    private readonly HttpClient _lena = Client(factory, "lena");
    private readonly TestData _data = new(factory);

    [Fact]
    public async Task Snapshot_scores_states_and_stays_unchanged_after_a_correction()
    {
        var year = Interlocked.Increment(ref _year);
        string v1 = $"MA/{year}/1", v2 = $"MA/{year}/2";
        await _importer.UploadAndWaitAsync("original.csv", Csv((v1, "FRA", "Y"), (v1, "BRA", "N"), (v1, "JPN", "A"), (v2, "FRA", "N"), (v2, "BRA", "Y"), (v2, "JPN", "X")));
        var votes = (await _dana.GetFromJsonAsync<List<VoteSummaryDto>>("/api/votes?from=2024-01-11&to=2024-01-11", ImportTestClient.Json))!;
        var id1 = votes.Single(v => v.AssemblyVoteId == v1).Id;
        var id2 = votes.Single(v => v.AssemblyVoteId == v2).Id;

        // POS-04: a vote without a position blocks the snapshot, with the reason on the selection.
        var blocked = await _dana.PostAsJsonAsync("/api/snapshots", new { name = "Q1", assemblyVoteIds = new[] { v1, v2 } });
        Assert.Equal(HttpStatusCode.BadRequest, blocked.StatusCode);
        Assert.Contains("snapshot.votes_without_position", await blocked.Content.ReadAsStringAsync());

        await SetPositionAsync(id1, "For", 4);
        await SetPositionAsync(id2, "For", 2);
        var first = await CreateAndWaitAsync("Q1", v1, v2);

        // Brief's example: YES on a weight-4 vote (match), NO on a weight-2 vote (no match): 100 × 4 ÷ 6 = 66.7.
        var france = Assert.Single(first.Ranking, r => r.StateCode == "FRA");
        Assert.Equal((66.7m, 1, 6, 4), (france.Score!.Value, france.Rank!.Value, france.CountedWeight, france.MatchingWeight));
        var brazil = Assert.Single(first.Ranking, r => r.StateCode == "BRA");
        Assert.Equal((33.3m, 2), (brazil.Score!.Value, brazil.Rank!.Value));
        var japan = Assert.Single(first.Ranking, r => r.StateCode == "JPN"); // only ABSTAIN / ABSENT: not counted
        Assert.Equal(((decimal?)null, (int?)null), (japan.Score, japan.Rank));
        Assert.NotEmpty(first.Rules);

        var detail = (await _dana.GetFromJsonAsync<SnapshotCountryDto>($"/api/snapshots/{first.Snapshot.Id}/countries/FRA", ImportTestClient.Json))!;
        Assert.Equal([(v1, true, true), (v2, true, false)], detail.Votes.Select(v => (v.AssemblyVoteId, v.Counted, v.Matching)));

        // A correction is accepted after the snapshot: FRA now voted NO on vote 1.
        await _importer.UploadAndWaitAsync("rev2.csv", Csv((v1, "FRA", "N")));
        var conflict = (await _dana.GetFromJsonAsync<List<ConflictSummaryDto>>($"/api/conflicts?voteId={id1}", ImportTestClient.Json))!.Single();
        Assert.True((await _dana.PostAsJsonAsync($"/api/conflicts/{conflict.Id}/resolution", new { decision = "Accept" })).IsSuccessStatusCode);

        var reopened = (await _dana.GetFromJsonAsync<SnapshotDto>($"/api/snapshots/{first.Snapshot.Id}", ImportTestClient.Json))!;
        Assert.Equal(66.7m, reopened.Ranking.Single(r => r.StateCode == "FRA").Score); // SNP-04: unchanged

        var second = await CreateAndWaitAsync("Q1 after corrections", v1, v2);
        Assert.Equal(0m, second.Ranking.Single(r => r.StateCode == "FRA").Score); // new snapshot uses the new approved value

        // SNP-05: admin publishes one snapshot per quarter; analysts cannot publish.
        Assert.Equal(HttpStatusCode.Forbidden, (await _dana.PostAsJsonAsync($"/api/snapshots/{first.Snapshot.Id}/publish", new { quarter = $"{year}-Q1" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _lena.PostAsJsonAsync($"/api/snapshots/{first.Snapshot.Id}/publish", new { quarter = $"{year}-Q1" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await _lena.PostAsJsonAsync($"/api/snapshots/{second.Snapshot.Id}/publish", new { quarter = $"{year}-Q1" })).StatusCode);

        // Database guard: a completed snapshot's results cannot be edited even with raw SQL.
        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            _data.ExecuteSqlAsync("UPDATE snapshot_results SET score = 100 WHERE snapshot_id = $1", first.Snapshot.Id));
        Assert.Equal("MV001", ex.SqlState);
    }

    /// <summary>
    /// Regression: NZL was a member until 2021-06-30. Its ballot on a vote of 2021-06-30 is valid; an accepted
    /// correction moves the vote to 2021-07-01. The approved ballot is kept, but it must not count in a snapshot.
    /// </summary>
    [Fact]
    public async Task Ballot_of_a_state_that_was_not_a_member_on_the_corrected_vote_date_is_not_counted()
    {
        var vote = $"MA/{Interlocked.Increment(ref _year)}/1";
        static byte[] File(string vote, string date, params (string Code, string Ballot)[] rows) => Encoding.UTF8.GetBytes(
            Header + "\n" + string.Concat(rows.Select(r => $"{vote},76,{date},Edge case,{r.Code},{r.Code},{r.Ballot},2021-07-02T09:00:00Z\n")));

        await _importer.UploadAndWaitAsync("original.csv", File(vote, "2021-06-30", ("NZL", "Y"), ("FRA", "Y"), ("BRA", "N")));
        await _importer.UploadAndWaitAsync("corrected.csv", File(vote, "2021-07-01", ("FRA", "Y"), ("BRA", "N")));
        var voteId = (await _dana.GetFromJsonAsync<List<VoteSummaryDto>>("/api/votes?from=2021-06-30&to=2021-07-01", ImportTestClient.Json))!
            .Single(v => v.AssemblyVoteId == vote).Id;
        var dateConflict = (await _dana.GetFromJsonAsync<List<ConflictSummaryDto>>($"/api/conflicts?voteId={voteId}", ImportTestClient.Json))!
            .Single(c => c.Field == Domain.Conflicts.ConflictField.VoteDate);
        Assert.True((await _dana.PostAsJsonAsync($"/api/conflicts/{dateConflict.Id}/resolution", new { decision = "Accept" })).IsSuccessStatusCode);
        await SetPositionAsync(voteId, "For", 3);

        var snapshot = await CreateAndWaitAsync("date correction", vote);

        var votes = (await _dana.GetFromJsonAsync<VoteDetailsDto>($"/api/votes/{voteId}", ImportTestClient.Json))!;
        Assert.Equal(new DateOnly(2021, 7, 1), votes.VoteDate);
        Assert.Contains(votes.CountryVotes, c => c.StateCode == "NZL"); // approved data untouched
        var nzl = snapshot.Ranking.Single(r => r.StateCode == "NZL");
        Assert.Equal(((decimal?)null, (int?)null, 0), (nzl.Score, nzl.Rank, nzl.CountedWeight));
        var france = snapshot.Ranking.Single(r => r.StateCode == "FRA");
        var brazil = snapshot.Ranking.Single(r => r.StateCode == "BRA");
        Assert.Equal((100.0m, 1, 3), (france.Score!.Value, france.Rank!.Value, france.CountedWeight));
        Assert.Equal((0.0m, 2), (brazil.Score!.Value, brazil.Rank!.Value));

        var breakdown = (await _dana.GetFromJsonAsync<SnapshotCountryDto>($"/api/snapshots/{snapshot.Snapshot.Id}/countries/NZL", ImportTestClient.Json))!;
        var entry = Assert.Single(breakdown.Votes); // the ballot is part of the frozen inputs, marked not counted
        Assert.Equal(("Y", false, false), (entry.Ballot, entry.Counted, entry.Matching));
    }

    private async Task<SnapshotDto> CreateAndWaitAsync(string name, params string[] assemblyVoteIds)
    {
        var response = await _dana.PostAsJsonAsync("/api/snapshots", new { name, assemblyVoteIds });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var id = (await response.Content.ReadFromJsonAsync<SnapshotDto>(ImportTestClient.Json))!.Snapshot.Id;
        for (var i = 0; i < 100; i++)
        {
            var snapshot = (await _dana.GetFromJsonAsync<SnapshotDto>($"/api/snapshots/{id}", ImportTestClient.Json))!;
            if (snapshot.Snapshot.Status != SnapshotStatus.Pending)
            {
                Assert.Equal(SnapshotStatus.Completed, snapshot.Snapshot.Status);
                return snapshot;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException($"Snapshot {id} was not calculated.");
    }

    private async Task SetPositionAsync(int voteId, string position, int weight)
    {
        var response = await _lena.PutAsJsonAsync($"/api/votes/{voteId}/position", new { position, weight, expectedRevision = 0 });
        response.EnsureSuccessStatusCode();
    }

    private static byte[] Csv(params (string Vote, string Code, string Ballot)[] rows)
    {
        var csv = new StringBuilder(Header).Append('\n');
        foreach (var (vote, code, ballot) in rows)
        {
            csv.Append($"{vote},79,2024-01-11,Test vote,{code},{code},{ballot},2024-04-02T09:00:00Z\n");
        }

        return Encoding.UTF8.GetBytes(csv.ToString());
    }

    private static HttpClient Client(ApiFactory factory, string userId)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(XUserAuthenticationHandler.HeaderName, userId);
        return client;
    }
}
