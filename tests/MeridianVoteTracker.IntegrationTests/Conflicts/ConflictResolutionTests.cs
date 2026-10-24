using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MeridianVoteTracker.Api.Auth;
using MeridianVoteTracker.Application.Conflicts;
using MeridianVoteTracker.Application.Votes;
using MeridianVoteTracker.Domain.Conflicts;
using MeridianVoteTracker.Domain.History;
using MeridianVoteTracker.Domain.Votes;
using MeridianVoteTracker.IntegrationTests.Infrastructure;

namespace MeridianVoteTracker.IntegrationTests.Conflicts;

/// <summary>Conflict review end to end over HTTP (CON-01..06, HIS-04, POS-01..03).</summary>
[Collection(ImportCollection.Name)]
public class ConflictResolutionTests(ImportApiFactory factory)
{
    private const string Header = "assembly_vote_id,session,vote_date,resolution_title,country_code,country_name,ballot,published_at";
    private static int _year = 5000;

    private readonly ImportTestClient _importer = new(factory);
    private readonly HttpClient _dana = Client(factory, "dana");
    private readonly HttpClient _omar = Client(factory, "omar");
    private readonly HttpClient _lena = Client(factory, "lena");

    [Fact]
    public async Task Accepting_changes_the_approved_value_keeps_history_and_a_second_decision_gets_409()
    {
        var voteId = await ImportVoteAsync(out var assemblyId, ("BRA", "Y"), ("IND", "Y"));
        await ImportAsync(Csv(assemblyId, "Revised title", ("BRA", "A"), ("IND", "Y")), "rev2.csv");

        var pending = await ListAsync(voteId);
        Assert.Equal(2, pending.Count);
        var ballot = Assert.Single(pending, c => c.Kind == ConflictKind.CountryBallot);
        var details = await _dana.GetFromJsonAsync<ConflictDto>($"/api/conflicts/{ballot.Id}", ImportTestClient.Json);
        Assert.Equal(("Y", "A", "Y"), (details!.ApprovedValueAtDetection, details.ProposedValue, details.CurrentApprovedValue));
        Assert.Equal(("original.csv", "rev2.csv", "dana"), (details.ApprovedSource.FileName, details.ProposedSource.FileName, details.ProposedSource.UploadedBy));

        var accepted = await ResolveAsync(_dana, ballot.Id, ConflictDecision.Accept, "Corrigendum of 18 April");
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var afterAccept = (await accepted.Content.ReadFromJsonAsync<ConflictDto>(ImportTestClient.Json))!;
        Assert.Equal((ConflictStatus.Accepted, "dana", "A"), (afterAccept.Status, afterAccept.ResolvedBy, afterAccept.CurrentApprovedValue));

        // Omar decides the same conflict afterwards: clear 409 with who decided, nothing overwritten (CON-05).
        var late = await ResolveAsync(_omar, ballot.Id, ConflictDecision.Reject, null);
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        using var problem = JsonDocument.Parse(await late.Content.ReadAsStringAsync());
        Assert.Equal(Domain.Common.DomainErrors.ConflictAlreadyResolved, problem.RootElement.GetProperty("code").GetString());
        Assert.Equal("dana", problem.RootElement.GetProperty("current").GetProperty("resolvedBy").GetString());
        Assert.Equal("Accepted", problem.RootElement.GetProperty("current").GetProperty("status").GetString());

        var vote = await _dana.GetFromJsonAsync<VoteDetailsDto>($"/api/votes/{voteId}", ImportTestClient.Json);
        var brazil = vote!.CountryVotes.Single(c => c.StateCode == "BRA");
        Assert.Equal((Ballot.Abstain, 2, ApprovalOrigin.ConflictResolution, "dana"), (brazil.Ballot, brazil.Revision, brazil.Origin, brazil.ApprovedBy));

        // Timeline: the old value is not lost; it shows where both values came from and who decided (HIS-02..04).
        var timeline = await _dana.GetFromJsonAsync<List<VoteHistoryEntryDto>>($"/api/votes/{voteId}/timeline", ImportTestClient.Json);
        var corrected = Assert.Single(timeline!, e => e.Type == VoteHistoryEventType.ApprovedValueCorrected);
        Assert.Equal(("Y", "A", "original.csv", "rev2.csv", "dana", "Corrigendum of 18 April"),
            (corrected.OldValue, corrected.NewValue, corrected.OldSourceFile, corrected.NewSourceFile, corrected.Actor, corrected.Comment));
        Assert.Contains(timeline!, e => e.Type == VoteHistoryEventType.VoteCreated);
        Assert.Equal(2, timeline!.Count(e => e.Type == VoteHistoryEventType.ConflictOpened));

        // Resolved conflicts remain viewable (CON-06); the title conflict is still pending.
        Assert.Contains(await ListAsync(voteId, ConflictStatus.Accepted), c => c.Id == ballot.Id);
        Assert.Single(await ListAsync(voteId));
    }

    [Fact]
    public async Task Concurrent_decisions_on_the_same_conflict_only_one_wins()
    {
        var voteId = await ImportVoteAsync(out var assemblyId, ("FRA", "Y"));
        await ImportAsync(Csv(assemblyId, null, ("FRA", "N")), "rev2.csv");
        var conflict = Assert.Single(await ListAsync(voteId));

        var responses = await Task.WhenAll(
            ResolveAsync(_dana, conflict.Id, ConflictDecision.Accept, null),
            ResolveAsync(_omar, conflict.Id, ConflictDecision.Reject, null));

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], responses.Select(r => r.StatusCode).Order());
        var final = await _dana.GetFromJsonAsync<ConflictDto>($"/api/conflicts/{conflict.Id}", ImportTestClient.Json);
        var winner = responses.Single(r => r.IsSuccessStatusCode).RequestMessage!.Headers.GetValues(XUserAuthenticationHandler.HeaderName).Single();
        Assert.Equal(winner, final!.ResolvedBy);
        Assert.Equal(final.Status == ConflictStatus.Accepted ? "N" : "Y", final.CurrentApprovedValue);
    }

    [Fact]
    public async Task Two_different_proposals_for_the_same_fact_accepting_one_supersedes_the_other()
    {
        var voteId = await ImportVoteAsync(out var assemblyId, ("JPN", "Y"));
        await ImportAsync(Csv(assemblyId, null, ("JPN", "A")), "file-a.csv");
        await ImportAsync(Csv(assemblyId, null, ("JPN", "N")), "file-b.csv");
        var proposals = await ListAsync(voteId);
        Assert.Equal(2, proposals.Count);

        var responses = await Task.WhenAll(proposals.Select(p => ResolveAsync(p.ProposedValue == "A" ? _dana : _omar, p.Id, ConflictDecision.Accept, null)));

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], responses.Select(r => r.StatusCode).Order());
        var vote = await _dana.GetFromJsonAsync<VoteDetailsDto>($"/api/votes/{voteId}", ImportTestClient.Json);
        var japan = vote!.CountryVotes.Single();
        Assert.Equal(2, japan.Revision); // exactly one change applied, no lost update
        Assert.Single(await ListAsync(voteId, ConflictStatus.Superseded));
    }

    [Fact]
    public async Task Accepting_a_metadata_conflict_changes_the_vote_details()
    {
        var voteId = await ImportVoteAsync(out var assemblyId, ("BRA", "Y"));
        await ImportAsync(Csv(assemblyId, "Corrected title", ("BRA", "Y")), "rev2.csv");
        var conflict = Assert.Single(await ListAsync(voteId));
        Assert.Equal(ConflictField.Title, conflict.Field);

        Assert.Equal(HttpStatusCode.OK, (await ResolveAsync(_dana, conflict.Id, ConflictDecision.Accept, null)).StatusCode);

        var vote = await _dana.GetFromJsonAsync<VoteDetailsDto>($"/api/votes/{voteId}", ImportTestClient.Json);
        Assert.Equal("Corrected title", vote!.Title);
    }

    [Fact]
    public async Task Only_admin_sets_position_with_field_validation_and_optimistic_concurrency()
    {
        var voteId = await ImportVoteAsync(out _, ("BRA", "Y"));
        var url = $"/api/votes/{voteId}/position";

        Assert.Equal(HttpStatusCode.Forbidden, (await _dana.PutAsJsonAsync(url, new { position = "For", weight = 4, expectedRevision = 0 })).StatusCode);

        var invalid = await _lena.PutAsJsonAsync(url, new { position = "For", weight = 7, expectedRevision = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using (var problem = JsonDocument.Parse(await invalid.Content.ReadAsStringAsync()))
        {
            Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty("weight", out _));
        }

        var saved = await _lena.PutAsJsonAsync(url, new { position = "For", weight = 4, expectedRevision = 0 });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal((Position.For, 4, 1), ((await saved.Content.ReadFromJsonAsync<VoteDetailsDto>(ImportTestClient.Json))! is var v ? (v.Position!.Value, v.Weight!.Value, v.PositionRevision) : default));

        Assert.Equal(HttpStatusCode.Conflict, (await _lena.PutAsJsonAsync(url, new { position = "Against", weight = 2, expectedRevision = 0 })).StatusCode);

        var timeline = await _lena.GetFromJsonAsync<List<VoteHistoryEntryDto>>($"/api/votes/{voteId}/timeline", ImportTestClient.Json);
        var change = Assert.Single(timeline!, e => e.Type == VoteHistoryEventType.PositionChanged);
        Assert.Equal((null, "For, weight 4", "lena"), (change.OldValue, change.NewValue, change.Actor));
    }

    private Task<int> ImportVoteAsync(out string assemblyId, params (string Code, string Ballot)[] ballots)
    {
        assemblyId = $"MA/{Interlocked.Increment(ref _year)}/1";
        var id = assemblyId;
        return ImportAndFindVoteAsync(id, Csv(id, null, ballots));
    }

    private async Task<int> ImportAndFindVoteAsync(string assemblyId, string csv)
    {
        await ImportAsync(csv, "original.csv");
        var votes = await _dana.GetFromJsonAsync<List<VoteSummaryDto>>("/api/votes?from=2024-01-11&to=2024-01-11", ImportTestClient.Json);
        return votes!.Single(v => v.AssemblyVoteId == assemblyId).Id;
    }

    private Task ImportAsync(string csv, string fileName) => _importer.UploadAndWaitAsync(fileName, Encoding.UTF8.GetBytes(csv));

    private static string Csv(string assemblyId, string? title, params (string Code, string Ballot)[] ballots)
    {
        var csv = new StringBuilder(Header).Append('\n');
        foreach (var (code, ballot) in ballots)
        {
            csv.Append($"{assemblyId},79,2024-01-11,{title ?? "Maritime Safety"},{code},{code},{ballot},2024-04-02T09:00:00Z\n");
        }

        return csv.ToString();
    }

    private async Task<List<ConflictSummaryDto>> ListAsync(int voteId, ConflictStatus status = ConflictStatus.Pending) =>
        (await _dana.GetFromJsonAsync<List<ConflictSummaryDto>>($"/api/conflicts?voteId={voteId}&status={status}", ImportTestClient.Json))!;

    private static Task<HttpResponseMessage> ResolveAsync(HttpClient client, Guid id, ConflictDecision decision, string? comment) =>
        client.PostAsJsonAsync($"/api/conflicts/{id}/resolution", new { decision = decision.ToString(), comment });

    private static HttpClient Client(ApiFactory factory, string userId)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(XUserAuthenticationHandler.HeaderName, userId);
        return client;
    }
}
