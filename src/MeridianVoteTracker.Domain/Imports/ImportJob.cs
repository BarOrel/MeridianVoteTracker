using MeridianVoteTracker.Domain.Common;

namespace MeridianVoteTracker.Domain.Imports;

public enum ImportJobStatus
{
    Queued,
    Processing,
    Completed,
    CompletedWithIssues,
    Failed,
}

public enum ImportPhase
{
    Pending,

    /// <summary>Streaming the file into import_rows. Restartable: a retry deletes this job's staged rows and starts over.</summary>
    Staging,

    /// <summary>Classifying staged rows vote by vote. Resumable: each vote batch commits approved data and row results together.</summary>
    Classifying,

    Finalized,
}

/// <summary>
/// One upload and its processing state; also the durable work item of the database-backed queue.
/// Lifecycle: Queued → Processing (Staging → Classifying) → Completed | CompletedWithIssues | Failed.
/// A finalized job and its rows are immutable (enforced by database triggers).
/// The lease lets a crashed worker's job be picked up again once the lease expires.
/// </summary>
public sealed class ImportJob
{
    public const int MaxAttempts = 3;
    public const int MaxErrorLength = 4000;

    public Guid Id { get; private set; }
    public string OriginalFileName { get; private set; } = null!;
    public string StorageKey { get; private set; } = null!;
    public string Sha256 { get; private set; } = null!;
    public long SizeBytes { get; private set; }
    public string Format { get; private set; } = null!;
    public string UploadedBy { get; private set; } = null!;
    public DateTimeOffset UploadedAt { get; private set; }
    public string? CorrelationId { get; private set; }

    public ImportJobStatus Status { get; private set; }
    public ImportPhase Phase { get; private set; }
    public int? TotalRows { get; private set; }
    public int StagedRows { get; private set; }
    public int ClassifiedRows { get; private set; }
    public int Attempts { get; private set; }
    public string? LeaseOwner { get; private set; }
    public DateTimeOffset? LeaseExpiresAt { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? FinishedAt { get; private set; }
    public string? Error { get; private set; }

    public bool IsFinalized => Status is ImportJobStatus.Completed or ImportJobStatus.CompletedWithIssues or ImportJobStatus.Failed;

    public bool HasAttemptsLeft => Attempts < MaxAttempts;

    /// <summary>True while <paramref name="workerId"/> owns the job in attempt <paramref name="attempt"/>.</summary>
    public bool IsOwnedBy(string workerId, int attempt) =>
        !IsFinalized && Status == ImportJobStatus.Processing && LeaseOwner == workerId && Attempts == attempt;

    private ImportJob() { }

    public static ImportJob Create(Guid id, string originalFileName, string storageKey, string sha256, long sizeBytes, string format, string uploadedBy, DateTimeOffset uploadedAt, string? correlationId) => new()
    {
        Id = id,
        OriginalFileName = originalFileName,
        StorageKey = storageKey,
        Sha256 = sha256,
        SizeBytes = sizeBytes,
        Format = format,
        UploadedBy = uploadedBy,
        UploadedAt = uploadedAt,
        CorrelationId = correlationId,
        Status = ImportJobStatus.Queued,
        Phase = ImportPhase.Pending,
    };

    /// <summary>A worker takes (or re-takes after a crash) ownership of the job for a limited time.</summary>
    public void BeginAttempt(string workerId, DateTimeOffset now, TimeSpan leaseDuration)
    {
        EnsureNotFinalized();
        if (Status == ImportJobStatus.Processing && LeaseExpiresAt > now)
        {
            throw new DomainException("import.leased", $"Import {Id} is being processed by {LeaseOwner} until {LeaseExpiresAt:O}.");
        }

        if (Attempts >= MaxAttempts)
        {
            throw new DomainException("import.max_attempts", $"Import {Id} already used {Attempts} attempts.");
        }

        Status = ImportJobStatus.Processing;
        Attempts++;
        LeaseOwner = workerId;
        LeaseExpiresAt = now + leaseDuration;
        StartedAt ??= now;

        // Staging is not resumable (rows are restaged from the file); classification is.
        if (Phase is ImportPhase.Pending or ImportPhase.Staging)
        {
            Phase = ImportPhase.Staging;
            StagedRows = 0;
            TotalRows = null;
        }
    }

    public void RecordStagingProgress(int stagedRows, DateTimeOffset now, TimeSpan leaseDuration)
    {
        EnsurePhase(ImportPhase.Staging);
        StagedRows = stagedRows;
        LeaseExpiresAt = now + leaseDuration;
    }

    public void CompleteStaging(int totalRows)
    {
        EnsurePhase(ImportPhase.Staging);
        TotalRows = totalRows;
        StagedRows = totalRows;
        Phase = ImportPhase.Classifying;
    }

    public void RecordClassificationProgress(int classifiedRows, DateTimeOffset now, TimeSpan leaseDuration)
    {
        EnsurePhase(ImportPhase.Classifying);
        ClassifiedRows = classifiedRows;
        LeaseExpiresAt = now + leaseDuration;
    }

    /// <summary>
    /// Finalizes the job. Persistence rule: this must be the last write of the import, after all
    /// row classifications are flushed, because the database freezes the rows of a finalized job.
    /// </summary>
    public void Complete(bool hasIssues, DateTimeOffset now)
    {
        EnsurePhase(ImportPhase.Classifying);
        Status = hasIssues ? ImportJobStatus.CompletedWithIssues : ImportJobStatus.Completed;
        Finalize(now);
    }

    public void Fail(string error, DateTimeOffset now)
    {
        EnsureNotFinalized();
        Status = ImportJobStatus.Failed;
        Error = Truncate(error);
        Finalize(now);
    }

    /// <summary>
    /// A transient failure (e.g. database unavailable): the error is visible to the user and the job
    /// becomes claimable again at <paramref name="retryAt"/> (the expiry of a released lease).
    /// </summary>
    public void RecordFailedAttempt(string error, DateTimeOffset retryAt)
    {
        EnsureNotFinalized();
        if (!HasAttemptsLeft)
        {
            throw new InvalidOperationException($"Import {Id} has no attempts left; fail it instead.");
        }

        Error = Truncate(error);
        LeaseOwner = null;
        LeaseExpiresAt = retryAt;
    }

    private static string Truncate(string error) => error.Length <= MaxErrorLength ? error : error[..MaxErrorLength];

    private void Finalize(DateTimeOffset now)
    {
        Phase = ImportPhase.Finalized;
        FinishedAt = now;
        LeaseOwner = null;
        LeaseExpiresAt = null;
    }

    private void EnsureNotFinalized()
    {
        if (IsFinalized)
        {
            throw new DomainException("import.finalized", $"Import {Id} is finalized ({Status}) and cannot change.");
        }
    }

    private void EnsurePhase(ImportPhase expected)
    {
        EnsureNotFinalized();
        if (Phase != expected)
        {
            throw new InvalidOperationException($"Import {Id} is in phase {Phase}, expected {expected}.");
        }
    }
}
