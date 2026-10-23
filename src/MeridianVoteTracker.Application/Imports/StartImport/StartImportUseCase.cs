using MeridianVoteTracker.Application.Abstractions;
using MeridianVoteTracker.Application.Common;
using MeridianVoteTracker.Application.Imports.Pipeline;
using MeridianVoteTracker.Application.Imports.Ports;
using MeridianVoteTracker.Domain.Imports;

namespace MeridianVoteTracker.Application.Imports.StartImport;

public sealed record StartImportCommand(string FileName, Stream Content, string? CorrelationId);

public sealed record StartImportResult(Guid ImportJobId);

/// <summary>
/// ING-01: accepts an upload and returns immediately. Stores the file durably, detects its format and
/// queues an <see cref="ImportJob"/>; all processing happens in the background worker.
/// Transaction: one (the job insert), after the file is stored. If the format is unsupported or the
/// insert fails, the stored file is deleted, so no orphan files and no jobs without files.
/// </summary>
public sealed class StartImportUseCase(
    IImportFileStorage storage,
    ImportFileReaderResolver readers,
    IApplicationDbContext db,
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    TimeProvider time)
{
    private const int MaxFileNameLength = 260;
    private const int DetectionHeadBytes = 4096;

    public async Task<StartImportResult> ExecuteAsync(StartImportCommand command, CancellationToken cancellationToken)
    {
        var fileName = Path.GetFileName(command.FileName ?? string.Empty).Trim();
        if (fileName.Length is 0 or > MaxFileNameLength)
        {
            throw UseCaseException.Validation("file", "file.name_invalid", $"A file name of 1 to {MaxFileNameLength} characters is required.");
        }

        var jobId = Guid.NewGuid();
        var stored = await storage.SaveAsync(jobId, fileName, command.Content, cancellationToken);
        try
        {
            if (stored.SizeBytes == 0)
            {
                throw UseCaseException.Validation("file", "file.empty", "The file is empty.");
            }

            var reader = readers.Detect(fileName, await ReadHeadAsync(stored.StorageKey, cancellationToken))
                ?? throw new UseCaseException(UseCaseErrorKind.UnsupportedMediaType, "file.unsupported_format",
                    $"'{fileName}' is not a supported import format. Supported: {string.Join(", ", readers.SupportedFormats)}.", "file");

            var job = ImportJob.Create(jobId, fileName, stored.StorageKey, stored.Sha256, stored.SizeBytes, reader.Format,
                currentUser.Id, time.GetUtcNow(), command.CorrelationId);
            await unitOfWork.ExecuteInTransactionAsync(_ =>
            {
                db.ImportJobs.Add(job);
                return Task.CompletedTask;
            }, cancellationToken);

            return new StartImportResult(jobId);
        }
        catch
        {
            await storage.DeleteAsync(stored.StorageKey, CancellationToken.None);
            throw;
        }
    }

    private async Task<byte[]> ReadHeadAsync(string storageKey, CancellationToken cancellationToken)
    {
        await using var stream = await storage.OpenReadAsync(storageKey, cancellationToken);
        var buffer = new byte[DetectionHeadBytes];
        var read = await stream.ReadAtLeastAsync(buffer, DetectionHeadBytes, throwOnEndOfStream: false, cancellationToken);
        return buffer[..read];
    }
}
