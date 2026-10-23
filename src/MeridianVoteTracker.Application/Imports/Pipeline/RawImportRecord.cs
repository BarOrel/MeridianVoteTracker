using MeridianVoteTracker.Domain.Imports;

namespace MeridianVoteTracker.Application.Imports.Pipeline;

/// <summary>
/// The format-independent output of every <see cref="Ports.IImportFileReader"/>: one source record with its
/// position, its original text and the eight export fields as text. Everything after the reader
/// (validation, staging, classification) works only with this type and never knows the file format.
/// </summary>
/// <param name="RowNumber">Position in the source as a user sees it (CSV: physical line, header = 1).</param>
/// <param name="OriginalText">The record exactly as it appeared in the source.</param>
/// <param name="Fields">Raw field values, or null when the record's structure could not be mapped.</param>
/// <param name="StructuralError">Why the record could not be mapped (wrong column count, broken quoting...).</param>
public sealed record RawImportRecord(int RowNumber, string OriginalText, ImportRawFields? Fields, RecordProblem? StructuralError)
{
    public static RawImportRecord Mapped(int rowNumber, string originalText, ImportRawFields fields) => new(rowNumber, originalText, fields, null);

    public static RawImportRecord Malformed(int rowNumber, string originalText, RecordProblem problem) => new(rowNumber, originalText, null, problem);
}

/// <summary>A reason code (stable, for filtering and reports) plus a human-readable message.</summary>
public sealed record RecordProblem(string Code, string Message);

/// <summary>
/// The whole file is unusable (missing header, unreadable, missing stored file). Not retried: the same
/// input would fail the same way. Individual bad records are never reported this way.
/// </summary>
public sealed class ImportFileException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
