using MeridianVoteTracker.Application.Imports.Ports;
using MeridianVoteTracker.Domain.Imports;
using MeridianVoteTracker.Domain.Votes;
using MeridianVoteTracker.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace MeridianVoteTracker.Infrastructure.Imports;

/// <summary>
/// Writes staged rows with PostgreSQL binary COPY on the unit of work's own connection and transaction,
/// so a batch is committed together with the job's progress (or not at all). Rows are never tracked by EF.
/// The import_rows triggers still apply (e.g. no rows can be added to a finalized job).
/// </summary>
internal sealed class PostgresImportStagingStore(AppDbContext db) : IImportStagingStore
{
    private const string CopyCommand = """
        COPY import_rows (
            import_job_id, row_number, original_text,
            raw_assembly_vote_id, raw_session, raw_vote_date, raw_resolution_title,
            raw_country_code, raw_country_name, raw_ballot, raw_published_at,
            assembly_vote_id, session, vote_date, title, country_code, country_name, ballot, published_at,
            normalization_notes, classification, reason_code, message)
        FROM STDIN (FORMAT BINARY)
        """;

    public async Task ResetAsync(Guid importJobId, CancellationToken cancellationToken)
    {
        EnsureTransaction();
        await db.ImportRows.Where(r => r.ImportJobId == importJobId).ExecuteDeleteAsync(cancellationToken);
    }

    public async Task WriteBatchAsync(IReadOnlyList<ImportRow> rows, CancellationToken cancellationToken)
    {
        EnsureTransaction();
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();

        await using var writer = await connection.BeginBinaryImportAsync(CopyCommand, cancellationToken);
        foreach (var row in rows)
        {
            await writer.StartRowAsync(cancellationToken);
            await writer.WriteAsync(row.ImportJobId, NpgsqlDbType.Uuid, cancellationToken);
            await writer.WriteAsync(row.RowNumber, NpgsqlDbType.Integer, cancellationToken);
            await writer.WriteAsync(row.OriginalText, NpgsqlDbType.Varchar, cancellationToken);

            await WriteText(writer, row.RawAssemblyVoteId, cancellationToken);
            await WriteText(writer, row.RawSession, cancellationToken);
            await WriteText(writer, row.RawVoteDate, cancellationToken);
            await WriteText(writer, row.RawResolutionTitle, cancellationToken);
            await WriteText(writer, row.RawCountryCode, cancellationToken);
            await WriteText(writer, row.RawCountryName, cancellationToken);
            await WriteText(writer, row.RawBallot, cancellationToken);
            await WriteText(writer, row.RawPublishedAt, cancellationToken);

            await Write(writer, row.AssemblyVoteId?.Value, NpgsqlDbType.Varchar, cancellationToken);
            await Write(writer, row.Session, NpgsqlDbType.Integer, cancellationToken);
            await Write(writer, row.VoteDate, NpgsqlDbType.Date, cancellationToken);
            await Write(writer, row.Title, NpgsqlDbType.Varchar, cancellationToken);
            await Write(writer, row.CountryCode?.Value, NpgsqlDbType.Char, cancellationToken);
            await Write(writer, row.CountryName, NpgsqlDbType.Varchar, cancellationToken);
            await Write(writer, row.Ballot?.ToCode(), NpgsqlDbType.Char, cancellationToken);
            await Write(writer, row.PublishedAt?.ToUniversalTime(), NpgsqlDbType.TimestampTz, cancellationToken);

            await writer.WriteAsync(row.NormalizationNotes.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text, cancellationToken);
            await Write(writer, row.Classification?.ToString(), NpgsqlDbType.Varchar, cancellationToken);
            await Write(writer, row.ReasonCode, NpgsqlDbType.Varchar, cancellationToken);
            await Write(writer, row.Message, NpgsqlDbType.Varchar, cancellationToken);
        }

        await writer.CompleteAsync(cancellationToken);
    }

    private static Task WriteText(NpgsqlBinaryImporter writer, string? value, CancellationToken cancellationToken) =>
        Write(writer, value, NpgsqlDbType.Text, cancellationToken);

    private static async Task Write<T>(NpgsqlBinaryImporter writer, T? value, NpgsqlDbType type, CancellationToken cancellationToken)
    {
        if (value is null)
        {
            await writer.WriteNullAsync(cancellationToken);
        }
        else
        {
            await writer.WriteAsync(value, type, cancellationToken);
        }
    }

    private void EnsureTransaction()
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Staging writes must run inside IUnitOfWork.ExecuteInTransactionAsync.");
        }
    }
}
