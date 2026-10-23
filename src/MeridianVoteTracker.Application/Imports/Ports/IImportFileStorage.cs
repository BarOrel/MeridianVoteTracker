namespace MeridianVoteTracker.Application.Imports.Ports;

/// <summary>
/// Durable storage of uploaded files. The upload request returns as soon as the file is stored; the
/// worker reads it later (possibly after a restart, possibly more than once on retry), and the file is
/// kept as part of the import's audit record (HIS-01). Local disk today; Azure Blob Storage is a
/// drop-in implementation.
/// </summary>
public interface IImportFileStorage
{
    /// <summary>Streams the content to storage, computing size and SHA-256 on the way (no full buffering).</summary>
    Task<StoredImportFile> SaveAsync(Guid importJobId, string fileName, Stream content, CancellationToken cancellationToken);

    /// <exception cref="Pipeline.ImportFileException">The stored file no longer exists.</exception>
    Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken);

    /// <summary>Removes a file whose upload was rejected before an import job referenced it.</summary>
    Task DeleteAsync(string storageKey, CancellationToken cancellationToken);
}

public sealed record StoredImportFile(string StorageKey, long SizeBytes, string Sha256);
