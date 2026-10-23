using MeridianVoteTracker.Application.Imports.Pipeline;

namespace MeridianVoteTracker.Application.Imports.Ports;

/// <summary>
/// The extension point for file formats. A reader knows only its format's syntax (delimiters, quoting,
/// encoding, header mapping) and turns a stream into <see cref="RawImportRecord"/>s, lazily, one at a time.
/// Adding Excel/JSON means adding one implementation and registering it; validation, staging and
/// classification are untouched.
/// <para>
/// Contract: a bad record is yielded with a <see cref="RawImportRecord.StructuralError"/>, never thrown.
/// Only a file that is unusable as a whole throws <see cref="ImportFileException"/>.
/// </para>
/// </summary>
public interface IImportFileReader
{
    /// <summary>Stored on the import job, so a retry uses the same reader even if detection rules change.</summary>
    string Format { get; }

    /// <summary>Format detection from the file name and the first bytes of the content.</summary>
    bool CanRead(string fileName, ReadOnlySpan<byte> head);

    IAsyncEnumerable<RawImportRecord> ReadAsync(Stream content, CancellationToken cancellationToken);
}
