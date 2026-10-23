using MeridianVoteTracker.Application.Abstractions;
using MeridianVoteTracker.Application.Common;
using MeridianVoteTracker.Domain.Imports;
using Microsoft.EntityFrameworkCore;

namespace MeridianVoteTracker.Application.Imports.GetImportStatus;

/// <summary>What the UI shows, derived from status, phase and lease.</summary>
public enum ImportProgressState
{
    Queued,
    Staging,
    RetryScheduled,
    AwaitingClassification,
    Classifying,
    Completed,
    CompletedWithIssues,
    Failed,
}

public sealed record ImportStatusDto(
    Guid Id,
    string FileName,
    string Format,
    long SizeBytes,
    string Sha256,
    string UploadedBy,
    DateTimeOffset UploadedAt,
    ImportProgressState State,
    ImportJobStatus Status,
    ImportPhase Phase,
    int? TotalRows,
    int StagedRows,
    int ClassifiedRows,
    int InvalidRows,
    int Attempts,
    int MaxAttempts,
    string? Error,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt);

/// <summary>
/// ING-02: live progress, read from the database (so it survives page refreshes and worker restarts).
/// Read-only; no transaction.
/// </summary>
public sealed class GetImportStatusUseCase(IApplicationDbContext db)
{
    public async Task<ImportStatusDto> ExecuteAsync(Guid importJobId, CancellationToken cancellationToken)
    {
        var job = await db.ImportJobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == importJobId, cancellationToken)
            ?? throw UseCaseException.NotFound("import.not_found", $"Import {importJobId} does not exist.");

        var invalidRows = await db.ImportRows.CountAsync(
            r => r.ImportJobId == importJobId && r.Classification == ImportClassification.Invalid, cancellationToken);

        return new ImportStatusDto(job.Id, job.OriginalFileName, job.Format, job.SizeBytes, job.Sha256, job.UploadedBy, job.UploadedAt,
            DeriveState(job), job.Status, job.Phase, job.TotalRows, job.StagedRows, job.ClassifiedRows, invalidRows,
            job.Attempts, ImportJob.MaxAttempts, job.Error, job.StartedAt, job.FinishedAt);
    }

    private static ImportProgressState DeriveState(ImportJob job) => job.Status switch
    {
        ImportJobStatus.Queued => ImportProgressState.Queued,
        ImportJobStatus.Completed => ImportProgressState.Completed,
        ImportJobStatus.CompletedWithIssues => ImportProgressState.CompletedWithIssues,
        ImportJobStatus.Failed => ImportProgressState.Failed,
        _ when job.LeaseOwner is null && job.LeaseExpiresAt is not null => ImportProgressState.RetryScheduled,
        _ when job.Phase == ImportPhase.Classifying && job.LeaseOwner is null => ImportProgressState.AwaitingClassification,
        _ when job.Phase == ImportPhase.Classifying => ImportProgressState.Classifying,
        _ => ImportProgressState.Staging,
    };
}
