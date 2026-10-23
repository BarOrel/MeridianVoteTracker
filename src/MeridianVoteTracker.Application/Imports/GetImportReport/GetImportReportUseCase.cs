using MeridianVoteTracker.Application.Abstractions;
using MeridianVoteTracker.Application.Imports.GetImportStatus;
using MeridianVoteTracker.Domain.Conflicts;
using MeridianVoteTracker.Domain.Imports;
using Microsoft.EntityFrameworkCore;

namespace MeridianVoteTracker.Application.Imports.GetImportReport;

public sealed record ImportSummaryDto(
    int TotalRows,
    int NewRows,
    int UnchangedRows,
    int DuplicateRows,
    int ExactDuplicateRows,
    int DuplicatesAfterNormalization,
    int InvalidRows,
    int ContradictionRows,
    int ConflictRows,
    int NormalizedRows,
    int VotesCreated,
    int ConflictsCreated,
    int BallotConflictsCreated,
    int MetadataConflictsCreated,
    int RowsLinkedToPendingConflicts,
    int RowsLinkedToRejectedConflicts);

public sealed record VoteOutcomeDto(
    string AssemblyVoteId,
    int? VoteId,
    string Outcome,
    int NewRows,
    int UnchangedRows,
    int DuplicateRows,
    int InvalidRows,
    int ContradictionRows,
    int ConflictRows,
    int ConflictsCreated,
    IReadOnlyList<string> Notes);

public sealed record ImportReportDto(ImportStatusDto Import, ImportSummaryDto Summary, IReadOnlyList<VoteOutcomeDto> Votes);

/// <summary>
/// ING-14/15: the persisted import report. Everything is derived from rows and outcomes that are immutable
/// once the import is finalized, so the report reads the same days later. Read-only; no transaction.
/// While an import is still running the same report shows the partial picture.
/// </summary>
public sealed class GetImportReportUseCase(IApplicationDbContext db, GetImportStatusUseCase getStatus)
{
    /// <summary>Outcome shown for a vote whose rows in this file were all Invalid (never classified per vote).</summary>
    public const string NoValidRowsOutcome = "NoValidRows";

    /// <summary>Groups Invalid rows whose vote id could not be read at all (e.g. wrong column count).</summary>
    public const string UnreadableVoteId = "(unreadable)";

    public async Task<ImportReportDto> ExecuteAsync(Guid importJobId, CancellationToken cancellationToken)
    {
        var status = await getStatus.ExecuteAsync(importJobId, cancellationToken);
        var rows = db.ImportRows.Where(r => r.ImportJobId == importJobId);

        var byClassification = await rows.GroupBy(r => r.Classification)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key ?? (ImportClassification)(-1), x => x.Count, cancellationToken);
        int Rows(ImportClassification c) => byClassification.GetValueOrDefault(c);

        var exactDuplicates = await rows.CountAsync(r => r.DuplicateKind == DuplicateKind.Exact, cancellationToken);
        var normalizedRows = await rows.CountAsync(r => EF.Property<List<string>>(r, "_normalizationNotes").Count > 0, cancellationToken);
        var conflictRowReasons = await rows.Where(r => r.Classification == ImportClassification.Conflict)
            .GroupBy(r => r.ReasonCode)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key ?? string.Empty, x => x.Count, cancellationToken);
        var createdConflicts = await db.Conflicts.Where(c => c.SourceImportJobId == importJobId)
            .GroupBy(c => c.Kind)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);

        var outcomes = await db.ImportVoteOutcomes.AsNoTracking()
            .Where(o => o.ImportJobId == importJobId)
            .ToListAsync(cancellationToken);
        // Invalid rows have no normalized vote id; group them by the raw id as published.
        var invalidByVote = await rows.Where(r => r.Classification == ImportClassification.Invalid)
            .GroupBy(r => r.RawAssemblyVoteId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        var invalidCounts = invalidByVote
            .GroupBy(x => string.IsNullOrWhiteSpace(x.Key) ? UnreadableVoteId : x.Key.Trim())
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Count));

        var votes = outcomes
            .Select(o => new VoteOutcomeDto(o.AssemblyVoteId.Value, o.VoteId, o.Outcome.ToString(), o.NewRows, o.UnchangedRows, o.DuplicateRows,
                invalidCounts.GetValueOrDefault(o.AssemblyVoteId.Value), o.ContradictionRows, o.ConflictRows, o.ConflictsCreated, o.Notes))
            .Concat(invalidCounts
                .Where(kv => outcomes.All(o => o.AssemblyVoteId.Value != kv.Key))
                .Select(kv => new VoteOutcomeDto(kv.Key, null, NoValidRowsOutcome, 0, 0, 0, kv.Value, 0, 0, 0, [])))
            .OrderBy(v => v.AssemblyVoteId, StringComparer.Ordinal)
            .ToList();

        var summary = new ImportSummaryDto(
            status.TotalRows ?? status.StagedRows,
            Rows(ImportClassification.New),
            Rows(ImportClassification.Unchanged),
            Rows(ImportClassification.Duplicate),
            exactDuplicates,
            Rows(ImportClassification.Duplicate) - exactDuplicates,
            Rows(ImportClassification.Invalid),
            Rows(ImportClassification.Contradiction),
            Rows(ImportClassification.Conflict),
            normalizedRows,
            outcomes.Count(o => o.Outcome == VoteImportOutcome.NewVote),
            createdConflicts.Values.Sum(),
            createdConflicts.GetValueOrDefault(ConflictKind.CountryBallot),
            createdConflicts.GetValueOrDefault(ConflictKind.VoteMetadata),
            conflictRowReasons.GetValueOrDefault("conflict.already_pending"),
            conflictRowReasons.GetValueOrDefault("conflict.previously_rejected"));

        return new ImportReportDto(status, summary, votes);
    }
}
