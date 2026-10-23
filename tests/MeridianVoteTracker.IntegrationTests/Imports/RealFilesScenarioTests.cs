using MeridianVoteTracker.Application.Imports.GetImportReport;
using MeridianVoteTracker.Application.Imports.GetImportStatus;
using MeridianVoteTracker.Domain.Conflicts;
using MeridianVoteTracker.Domain.Imports;
using MeridianVoteTracker.Domain.MemberStates;
using MeridianVoteTracker.Domain.Votes;
using MeridianVoteTracker.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace MeridianVoteTracker.IntegrationTests.Imports;

/// <summary>
/// The assignment's own files, uploaded in the recommended order on an empty database:
/// q1 (first import) → rev2 (corrections: conflicts, completions, a new vote) → rev2 again (idempotency).
/// Expected numbers were derived independently (a separate re-implementation of the agreed rules) before
/// the classifier was written; see DECISIONS.md for the rules.
/// </summary>
[Collection(RealFilesCollection.Name)]
public class RealFilesScenarioTests(RealFilesApiFactory factory)
{
    private readonly ImportTestClient _dana = new(factory);
    private readonly ImportTestClient _omar = new(factory, "omar");
    private readonly TestData _data = new(factory);

    [Fact]
    public async Task Q1_then_rev2_then_rev2_again()
    {
        // ---- 1. Original Q1 export ------------------------------------------------------------
        var q1 = await UploadAsync(_dana, "assembly_export_2024_q1.csv");
        Assert.Equal(ImportProgressState.CompletedWithIssues, q1.Import.State);
        AssertSummary(q1.Summary, total: 5896, @new: 5731, unchanged: 0, duplicate: 3, invalid: 11, contradiction: 151, conflict: 0);
        Assert.Equal((3, 0, 1), (q1.Summary.ExactDuplicateRows, q1.Summary.DuplicatesAfterNormalization, q1.Summary.NormalizedRows));
        Assert.Equal((39, 0), (q1.Summary.VotesCreated, q1.Summary.ConflictsCreated));

        var blocked = Vote(q1, "MA/2024/025"); // three title variants: nothing of this vote stored
        Assert.Equal((nameof(VoteImportOutcome.BlockedByContradiction), 149), (blocked.Outcome, blocked.ContradictionRows));
        Assert.Equal(2, Vote(q1, "MA/2024/012").ContradictionRows); // FRA Y and N
        Assert.Equal((143, 6), (Vote(q1, "MA/2024/027").NewRows, Vote(q1, "MA/2024/027").InvalidRows)); // 21/02/2024 rows
        Assert.Equal(80, Vote(q1, "MA/2024/031").NewRows); // partial vote
        Assert.Equal(3, Vote(q1, "MA/2024/007").DuplicateRows);

        var problems = await _dana.GetRowsAsync(q1.Import.Id, "?pageSize=500");
        Assert.Equal(3 + 11 + 151, problems.TotalCount);
        Assert.Contains(problems.Rows, r => r.RowNumber == 1045 && r.DuplicateOfRowNumber == 896);
        var normalized = Assert.Single((await _dana.GetRowsAsync(q1.Import.Id, "?classification=New&normalizedOnly=true")).Rows);
        Assert.Equal((2594, "DEU"), (normalized.RowNumber, normalized.CountryCode));

        await using (var scope = _data.OpenScope())
        {
            Assert.Equal(39, await scope.Db.Votes.CountAsync());
            Assert.Equal(5731, await scope.Db.CountryVotes.CountAsync());
            Assert.False(await scope.Db.Votes.AnyAsync(v => v.AssemblyVoteId == AssemblyVoteId.Create("MA/2024/025")));
        }

        // ---- 2. Corrected export (rev2) -------------------------------------------------------
        var rev2 = await UploadAsync(_omar, "assembly_export_2024_q1_rev2.csv");
        Assert.Equal(ImportProgressState.CompletedWithIssues, rev2.Import.State);
        AssertSummary(rev2.Summary, total: 5960, @new: 378, unchanged: 5576, duplicate: 0, invalid: 0, contradiction: 0, conflict: 6);
        Assert.Equal((8, 6, 2), (rev2.Summary.ConflictsCreated, rev2.Summary.BallotConflictsCreated, rev2.Summary.MetadataConflictsCreated));
        Assert.Equal(2, rev2.Summary.VotesCreated); // MA/2024/025 (now consistent) and MA/2024/041 (new)
        Assert.Equal(nameof(VoteImportOutcome.CountriesAdded), Vote(rev2, "MA/2024/031").Outcome); // 69 missing states completed
        Assert.Equal(69, Vote(rev2, "MA/2024/031").NewRows);
        Assert.Equal(1, Vote(rev2, "MA/2024/012").NewRows); // FRA: no value existed, now one clear value
        Assert.DoesNotContain(rev2.Votes, v => v.AssemblyVoteId == "MA/2024/040"); // absent from the file: untouched
        Assert.Contains(Vote(rev2, "MA/2024/001").Notes, n => n.Contains("\"Turkey\""));

        await using (var scope = _data.OpenScope())
        {
            var pending = await scope.Db.Conflicts.AsNoTracking().Where(c => c.Status == ConflictStatus.Pending).ToListAsync();
            var voteIds = await scope.Db.Votes.ToDictionaryAsync(v => v.Id, v => v.AssemblyVoteId.Value);
            var described = pending
                .Select(c => $"{voteIds[c.VoteId]} {(c.StateCode is { } s ? s.Value : c.Field.ToString())} {c.ApprovedValueAtDetection}->{c.ProposedValue}")
                .Order(StringComparer.Ordinal)
                .ToList();
            Assert.Equal(
            [
                "MA/2024/003 BRA Y->A", "MA/2024/003 IND Y->N",
                "MA/2024/009 Title Biological Diversity in Coastal Zones->Biological Diversity in Coastal Zones: Revised",
                "MA/2024/010 JPN Y->X",
                "MA/2024/014 VoteDate 2024-02-05->2024-02-06",
                "MA/2024/019 ZAF Y->N", "MA/2024/028 CAN Y->N", "MA/2024/028 MEX Y->A",
            ], described);

            // Approved data was not changed by the corrected file (ING-08).
            var vote003 = voteIds.Single(v => v.Value == "MA/2024/003").Key;
            var brazil = await scope.Db.CountryVotes.AsNoTracking()
                .SingleAsync(cv => cv.VoteId == vote003 && cv.StateCode == CountryCode.Create("BRA"));
            Assert.Equal((Ballot.Yes, 1, q1.Import.Id), (brazil.Ballot, brazil.Revision, brazil.Provenance.SourceImportJobId));
            var vote014 = await scope.Db.Votes.AsNoTracking().SingleAsync(v => v.AssemblyVoteId == AssemblyVoteId.Create("MA/2024/014"));
            Assert.Equal(new DateOnly(2024, 2, 5), vote014.VoteDate);

            Assert.Equal(41, await scope.Db.Votes.CountAsync());
            Assert.Equal(5731 + 378, await scope.Db.CountryVotes.CountAsync());
            Assert.Equal(8, await scope.Db.VoteHistoryEvents.CountAsync(e => e.Type == Domain.History.VoteHistoryEventType.ConflictOpened));
        }

        // ---- 3. Same corrected export again ---------------------------------------------------
        var again = await UploadAsync(_dana, "assembly_export_2024_q1_rev2.csv");
        AssertSummary(again.Summary, total: 5960, @new: 0, unchanged: 5954, duplicate: 0, invalid: 0, contradiction: 0, conflict: 6);
        Assert.Equal((0, 0, 6), (again.Summary.ConflictsCreated, again.Summary.VotesCreated, again.Summary.RowsLinkedToPendingConflicts));
        Assert.Contains(Vote(again, "MA/2024/009").Notes, n => n.Contains("Already waiting for a decision"));

        await using (var scope = _data.OpenScope())
        {
            Assert.Equal(41, await scope.Db.Votes.CountAsync());
            Assert.Equal(5731 + 378, await scope.Db.CountryVotes.CountAsync());
            Assert.Equal(8, await scope.Db.Conflicts.CountAsync(c => c.Status == ConflictStatus.Pending));
        }

        // Reports are persisted: the first import's report is unchanged after later imports (ING-15).
        var q1Later = await _dana.GetReportAsync(q1.Import.Id);
        Assert.Equal(q1.Summary, q1Later.Summary);
    }

    private static async Task<ImportReportDto> UploadAsync(ImportTestClient client, string file)
    {
        var status = await client.UploadAndWaitAsync(file, await File.ReadAllBytesAsync(AssignmentData.PathOf(file)), TimeSpan.FromMinutes(2));
        return await client.GetReportAsync(status.Id);
    }

    private static VoteOutcomeDto Vote(ImportReportDto report, string id) => Assert.Single(report.Votes, v => v.AssemblyVoteId == id);

    private static void AssertSummary(ImportSummaryDto s, int total, int @new, int unchanged, int duplicate, int invalid, int contradiction, int conflict)
    {
        Assert.Equal(
            (total, @new, unchanged, duplicate, invalid, contradiction, conflict),
            (s.TotalRows, s.NewRows, s.UnchangedRows, s.DuplicateRows, s.InvalidRows, s.ContradictionRows, s.ConflictRows));
        Assert.Equal(total, @new + unchanged + duplicate + invalid + contradiction + conflict); // exactly one classification per row
    }
}
