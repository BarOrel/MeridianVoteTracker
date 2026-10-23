using MeridianVoteTracker.Application.Imports.Pipeline;
using MeridianVoteTracker.Application.Imports.Ports;
using MeridianVoteTracker.Infrastructure.Imports.Csv;

namespace MeridianVoteTracker.UnitTests.Imports;

public class ImportFileReaderResolverTests
{
    /// <summary>Stands in for a future format: registering it is all it takes to be detected.</summary>
    private sealed class FakeJsonReader : IImportFileReader
    {
        public string Format => "json";
        public bool CanRead(string fileName, ReadOnlySpan<byte> head) => fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
        public IAsyncEnumerable<RawImportRecord> ReadAsync(Stream content, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private readonly ImportFileReaderResolver _resolver = new([new CsvImportFileReader(), new FakeJsonReader()]);

    [Fact]
    public void Detects_reader_by_content_and_name()
    {
        Assert.Equal("csv", _resolver.Detect("q1.csv", "assembly_vote_id,"u8)!.Format);
        Assert.Equal("json", _resolver.Detect("q1.json", "{"u8)!.Format);
        Assert.Null(_resolver.Detect("q1.xlsx", "PK"u8));
    }

    [Fact]
    public void Unknown_stored_format_is_a_file_level_error() =>
        Assert.Equal("format.unsupported", Assert.Throws<ImportFileException>(() => _resolver.ForFormat("xml")).Code);
}
