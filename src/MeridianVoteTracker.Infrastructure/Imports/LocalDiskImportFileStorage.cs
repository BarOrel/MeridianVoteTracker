using System.Security.Cryptography;
using System.Text.RegularExpressions;
using MeridianVoteTracker.Application.Imports.Pipeline;
using MeridianVoteTracker.Application.Imports.Ports;
using Microsoft.Extensions.Options;

namespace MeridianVoteTracker.Infrastructure.Imports;

public sealed class ImportStorageOptions
{
    public const string SectionName = "ImportStorage";

    /// <summary>Directory for uploaded files; a Docker volume in Compose so files survive restarts.</summary>
    public string RootPath { get; set; } = "storage";
}

/// <summary>
/// Stores uploads on local (volume-backed) disk. Writes to a temporary file, flushes to disk and then
/// renames, so a stored file is always complete. The storage key is generated from the job id, never from
/// user input, so a file name cannot escape the root directory.
/// </summary>
internal sealed partial class LocalDiskImportFileStorage(IOptions<ImportStorageOptions> options) : IImportFileStorage
{
    private const int BufferSize = 81920;

    private string Root => Path.GetFullPath(options.Value.RootPath);

    public async Task<StoredImportFile> SaveAsync(Guid importJobId, string fileName, Stream content, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        var storageKey = $"{importJobId:N}{(SafeExtension().IsMatch(extension) ? extension : ".bin")}";
        var finalPath = PathFor(storageKey);
        var tempPath = Path.Combine(Root, ".incoming", $"{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);

        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long size = 0;
        try
        {
            await using (var target = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous))
            {
                var buffer = new byte[BufferSize];
                int read;
                while ((read = await content.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    sha256.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    size += read;
                }

                target.Flush(flushToDisk: true);
            }

            File.Move(tempPath, finalPath, overwrite: false);
        }
        finally
        {
            File.Delete(tempPath);
        }

        return new StoredImportFile(storageKey, size, Convert.ToHexString(sha256.GetHashAndReset()).ToLowerInvariant());
    }

    public Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken)
    {
        var path = PathFor(storageKey);
        if (!File.Exists(path))
        {
            throw new ImportFileException("file.missing", $"The stored file '{storageKey}' no longer exists.");
        }

        Stream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Task.FromResult(stream);
    }

    public Task DeleteAsync(string storageKey, CancellationToken cancellationToken)
    {
        File.Delete(PathFor(storageKey));
        return Task.CompletedTask;
    }

    private string PathFor(string storageKey)
    {
        if (!StorageKey().IsMatch(storageKey))
        {
            throw new ArgumentException($"Invalid storage key '{storageKey}'.", nameof(storageKey));
        }

        Directory.CreateDirectory(Root);
        return Path.Combine(Root, storageKey);
    }

    [GeneratedRegex(@"^\.[a-z0-9]{1,8}$")]
    private static partial Regex SafeExtension();

    [GeneratedRegex(@"^[0-9a-f]{32}\.[a-z0-9]{1,8}$")]
    private static partial Regex StorageKey();
}
