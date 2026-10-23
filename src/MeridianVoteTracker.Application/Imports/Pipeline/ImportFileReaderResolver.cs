using MeridianVoteTracker.Application.Imports.Ports;

namespace MeridianVoteTracker.Application.Imports.Pipeline;

/// <summary>
/// Chooses the reader for an upload (by name and content) and for a stored job (by recorded format).
/// A plain class over the registered readers: selection logic has no infrastructure dependency and needs
/// no interface of its own.
/// </summary>
public sealed class ImportFileReaderResolver(IEnumerable<IImportFileReader> readers)
{
    private readonly IReadOnlyList<IImportFileReader> _readers = readers.ToList();

    public IReadOnlyList<string> SupportedFormats => _readers.Select(r => r.Format).ToList();

    public IImportFileReader? Detect(string fileName, ReadOnlySpan<byte> head)
    {
        foreach (var reader in _readers)
        {
            if (reader.CanRead(fileName, head))
            {
                return reader;
            }
        }

        return null;
    }

    public IImportFileReader ForFormat(string format) =>
        _readers.SingleOrDefault(r => r.Format == format)
        ?? throw new ImportFileException("format.unsupported", $"No reader is registered for format '{format}'.");
}
