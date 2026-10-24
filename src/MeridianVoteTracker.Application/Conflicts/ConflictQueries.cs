using MeridianVoteTracker.Application.Abstractions;
using MeridianVoteTracker.Application.Common;
using MeridianVoteTracker.Domain.Conflicts;
using MeridianVoteTracker.Domain.Votes;
using Microsoft.EntityFrameworkCore;

namespace MeridianVoteTracker.Application.Conflicts;

public sealed record ConflictSummaryDto(
    Guid Id,
    ConflictKind Kind,
    ConflictStatus Status,
    int VoteId,
    string AssemblyVoteId,
    string? StateCode,
    ConflictField Field,
    string ApprovedValue,
    string ProposedValue,
    DateTimeOffset DetectedAt,
    string SourceFileName,
    string? ResolvedBy,
    DateTimeOffset? ResolvedAt);

/// <summary>Where a value came from: the import file, who uploaded it and when (CON-02).</summary>
public sealed record ValueSourceDto(Guid ImportJobId, string FileName, string UploadedBy, DateTimeOffset UploadedAt, DateTimeOffset? PublishedAt, int? RowNumber);

public sealed record ConflictDto(
    Guid Id,
    ConflictKind Kind,
    ConflictStatus Status,
    int VoteId,
    string AssemblyVoteId,
    string VoteTitle,
    int Session,
    DateOnly VoteDate,
    string? StateCode,
    string? StateName,
    ConflictField Field,
    string ApprovedValueAtDetection,
    string ProposedValue,
    string CurrentApprovedValue,
    ValueSourceDto ApprovedSource,
    string ApprovedBy,
    DateTimeOffset ApprovedAt,
    ValueSourceDto ProposedSource,
    DateTimeOffset DetectedAt,
    string? ResolvedBy,
    DateTimeOffset? ResolvedAt,
    string? ResolutionComment,
    Guid? SupersededByConflictId);

/// <summary>CON-01: conflicts, pending by default, optionally for one vote. Resolved ones stay listable (CON-06).</summary>
public sealed class ListConflictsUseCase(IApplicationDbContext db)
{
    public async Task<IReadOnlyList<ConflictSummaryDto>> ExecuteAsync(ConflictStatus? status, int? voteId, CancellationToken cancellationToken)
    {
        var conflicts = db.Conflicts;
        if (status is { } s)
        {
            conflicts = conflicts.Where(c => c.Status == s);
        }

        if (voteId is { } v)
        {
            conflicts = conflicts.Where(c => c.VoteId == v);
        }

        var rows = await conflicts
            .Join(db.Votes, c => c.VoteId, vote => vote.Id, (c, vote) => new { c, vote.AssemblyVoteId })
            .Join(db.ImportJobs, x => x.c.SourceImportJobId, j => j.Id, (x, j) => new { x.c, x.AssemblyVoteId, j.OriginalFileName })
            .ToListAsync(cancellationToken);

        return rows
            .OrderBy(x => x.AssemblyVoteId.Value, StringComparer.Ordinal).ThenBy(x => x.c.Kind).ThenBy(x => x.c.StateCode?.Value)
            .Select(x => new ConflictSummaryDto(x.c.Id, x.c.Kind, x.c.Status, x.c.VoteId, x.AssemblyVoteId.Value, x.c.StateCode?.Value, x.c.Field,
                x.c.ApprovedValueAtDetection, x.c.ProposedValue, x.c.DetectedAt, x.OriginalFileName, x.c.ResolvedBy, x.c.ResolvedAt))
            .ToList();
    }
}

/// <summary>CON-02: approved vs. proposed value side by side, with the source of each and the decision if any.</summary>
public sealed class GetConflictUseCase(IApplicationDbContext db)
{
    public async Task<ConflictDto> ExecuteAsync(Guid conflictId, CancellationToken cancellationToken)
    {
        var conflict = await db.Conflicts.SingleOrDefaultAsync(c => c.Id == conflictId, cancellationToken)
            ?? throw UseCaseException.NotFound("conflict.not_found", $"Conflict {conflictId} does not exist.");
        var vote = await db.Votes.SingleAsync(v => v.Id == conflict.VoteId, cancellationToken);

        string currentValue;
        ApprovalProvenance provenance;
        string? stateName = null;
        if (conflict.Kind == ConflictKind.CountryBallot)
        {
            var approved = await db.CountryVotes.SingleAsync(cv => cv.VoteId == conflict.VoteId && cv.StateCode == conflict.StateCode, cancellationToken);
            currentValue = approved.Ballot.ToCode();
            provenance = approved.Provenance;
            stateName = await db.MemberStates.Where(m => m.Code == conflict.StateCode).Select(m => m.Name).SingleOrDefaultAsync(cancellationToken);
        }
        else
        {
            currentValue = vote.GetMetadataValue(conflict.Field);
            provenance = vote.MetadataProvenance;
        }

        var jobIds = new[] { provenance.SourceImportJobId, conflict.SourceImportJobId };
        var jobs = await db.ImportJobs.AsNoTracking().Where(j => jobIds.Contains(j.Id)).ToDictionaryAsync(j => j.Id, cancellationToken);
        var publishedAt = await db.ImportRows
            .Where(r => (r.ImportJobId == provenance.SourceImportJobId && r.RowNumber == provenance.SourceRowNumber)
                || (r.ImportJobId == conflict.SourceImportJobId && r.RowNumber == conflict.SourceRowNumber))
            .Select(r => new { r.ImportJobId, r.PublishedAt })
            .ToListAsync(cancellationToken);

        ValueSourceDto Source(Guid jobId, int? row)
        {
            var job = jobs[jobId];
            return new ValueSourceDto(job.Id, job.OriginalFileName, job.UploadedBy, job.UploadedAt,
                publishedAt.FirstOrDefault(p => p.ImportJobId == jobId)?.PublishedAt, row);
        }

        return new ConflictDto(conflict.Id, conflict.Kind, conflict.Status, vote.Id, vote.AssemblyVoteId.Value, vote.Title, vote.Session, vote.VoteDate,
            conflict.StateCode?.Value, stateName, conflict.Field, conflict.ApprovedValueAtDetection, conflict.ProposedValue, currentValue,
            Source(provenance.SourceImportJobId, provenance.SourceRowNumber), provenance.ApprovedBy, provenance.ApprovedAt,
            Source(conflict.SourceImportJobId, conflict.SourceRowNumber), conflict.DetectedAt,
            conflict.ResolvedBy, conflict.ResolvedAt, conflict.ResolutionComment, conflict.SupersededByConflictId);
    }
}
