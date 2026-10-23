using MeridianVoteTracker.Application.Abstractions;
using MeridianVoteTracker.Application.Common;
using MeridianVoteTracker.Domain.Imports;
using Microsoft.EntityFrameworkCore;

namespace MeridianVoteTracker.Application.Imports.GetImportReport;

public sealed record ImportRowsQuery(Guid ImportJobId, ImportClassification? Classification, bool NormalizedOnly, int Page = 1, int PageSize = 100);

public sealed record ImportRowDto(
    int RowNumber,
    ImportClassification? Classification,
    string? ReasonCode,
    string? Message,
    string? AssemblyVoteId,
    string? CountryCode,
    string? Ballot,
    int? DuplicateOfRowNumber,
    DuplicateKind? DuplicateKind,
    Guid? ConflictId,
    string OriginalText,
    IReadOnlyList<string> NormalizationNotes);

public sealed record ImportRowsPageDto(int Page, int PageSize, int TotalCount, IReadOnlyList<ImportRowDto> Rows);

/// <summary>
/// The row-level part of the report (ING-14, HIS-01): row number, outcome, reason, original value.
/// Without a classification filter it lists the rows that were not stored as-is (Duplicate, Invalid,
/// Contradiction, Conflict). Read-only; paged because the historic file has ~1M rows.
/// </summary>
public sealed class ListImportRowsUseCase(IApplicationDbContext db)
{
    private const int MaxPageSize = 500;

    private static readonly ImportClassification[] NotStoredAsIs =
        [ImportClassification.Duplicate, ImportClassification.Invalid, ImportClassification.Contradiction, ImportClassification.Conflict];

    public async Task<ImportRowsPageDto> ExecuteAsync(ImportRowsQuery query, CancellationToken cancellationToken)
    {
        if (query.Page < 1 || query.PageSize is < 1 or > MaxPageSize)
        {
            throw UseCaseException.Validation("pageSize", "paging.invalid", $"page must be >= 1 and pageSize between 1 and {MaxPageSize}.");
        }

        if (!await db.ImportJobs.AnyAsync(j => j.Id == query.ImportJobId, cancellationToken))
        {
            throw UseCaseException.NotFound("import.not_found", $"Import {query.ImportJobId} does not exist.");
        }

        var rows = db.ImportRows.AsNoTracking().Where(r => r.ImportJobId == query.ImportJobId);
        rows = query.Classification is { } classification
            ? rows.Where(r => r.Classification == classification)
            : rows.Where(r => NotStoredAsIs.Contains(r.Classification!.Value));
        if (query.NormalizedOnly)
        {
            rows = rows.Where(r => EF.Property<List<string>>(r, "_normalizationNotes").Count > 0);
        }

        var total = await rows.CountAsync(cancellationToken);
        var page = await rows.OrderBy(r => r.RowNumber)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);

        return new ImportRowsPageDto(query.Page, query.PageSize, total, page.Select(r => new ImportRowDto(
            r.RowNumber, r.Classification, r.ReasonCode, r.Message,
            r.AssemblyVoteId?.Value ?? r.RawAssemblyVoteId,
            r.CountryCode?.Value ?? r.RawCountryCode,
            r.RawBallot,
            r.DuplicateOfRowNumber, r.DuplicateKind, r.ConflictId, r.OriginalText, r.NormalizationNotes)).ToList());
    }
}
