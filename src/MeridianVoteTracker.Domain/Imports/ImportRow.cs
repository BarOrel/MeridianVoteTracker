using MeridianVoteTracker.Domain.MemberStates;
using MeridianVoteTracker.Domain.Votes;

namespace MeridianVoteTracker.Domain.Imports;

/// <summary>Exactly one per row (ING-06).</summary>
public enum ImportClassification
{
    New,
    Unchanged,
    Duplicate,
    Invalid,
    Contradiction,
    Conflict,
}

/// <summary>See DECISIONS.md, "Exact duplicates vs. equivalent after normalization".</summary>
public enum DuplicateKind
{
    /// <summary>Every raw field is byte-for-byte identical to an earlier row of the same file.</summary>
    Exact,

    /// <summary>Raw text differs (e.g. "deu" vs "DEU", surrounding spaces) but the normalized record is identical.</summary>
    AfterNormalization,
}

/// <summary>The eight fields exactly as the reader produced them: no trimming, no case changes.</summary>
public sealed record ImportRawFields(
    string? AssemblyVoteId,
    string? Session,
    string? VoteDate,
    string? ResolutionTitle,
    string? CountryCode,
    string? CountryName,
    string? Ballot,
    string? PublishedAt);

/// <summary>The validated, typed values used for classification and approved data.</summary>
public sealed record NormalizedImportRecord(
    AssemblyVoteId AssemblyVoteId,
    int Session,
    DateOnly VoteDate,
    string Title,
    CountryCode CountryCode,
    string CountryName,
    Ballot Ballot,
    DateTimeOffset? PublishedAt);

/// <summary>
/// The audit record of one source row (HIS-01). Original text and raw fields are kept forever,
/// independent of the normalized values; every normalization is listed in <see cref="NormalizationNotes"/>.
/// <para>
/// Lifecycle: <b>Staged</b> (valid, awaiting classification, or already Invalid) → <b>Classified</b> (once).
/// While the job is processing, rows may be deleted and restaged (crash during staging) or classified;
/// once the job is finalized the database rejects any INSERT/UPDATE/DELETE of its rows.
/// </para>
/// </summary>
public sealed class ImportRow
{
    private List<string> _normalizationNotes = [];

    public Guid ImportJobId { get; private set; }
    public int RowNumber { get; private set; }
    public string OriginalText { get; private set; } = null!;

    public string? RawAssemblyVoteId { get; private set; }
    public string? RawSession { get; private set; }
    public string? RawVoteDate { get; private set; }
    public string? RawResolutionTitle { get; private set; }
    public string? RawCountryCode { get; private set; }
    public string? RawCountryName { get; private set; }
    public string? RawBallot { get; private set; }
    public string? RawPublishedAt { get; private set; }

    public AssemblyVoteId? AssemblyVoteId { get; private set; }
    public int? Session { get; private set; }
    public DateOnly? VoteDate { get; private set; }
    public string? Title { get; private set; }
    public CountryCode? CountryCode { get; private set; }
    public string? CountryName { get; private set; }
    public Ballot? Ballot { get; private set; }
    public DateTimeOffset? PublishedAt { get; private set; }
    public IReadOnlyList<string> NormalizationNotes => _normalizationNotes;

    public ImportClassification? Classification { get; private set; }
    public string? ReasonCode { get; private set; }
    public string? Message { get; private set; }
    public int? DuplicateOfRowNumber { get; private set; }
    public DuplicateKind? DuplicateKind { get; private set; }
    public Guid? ConflictId { get; private set; }

    public bool IsNormalized => AssemblyVoteId is not null;

    private ImportRow() { }

    public ImportRawFields Raw => new(RawAssemblyVoteId, RawSession, RawVoteDate, RawResolutionTitle, RawCountryCode, RawCountryName, RawBallot, RawPublishedAt);

    public NormalizedImportRecord? Normalized => IsNormalized
        ? new(AssemblyVoteId!.Value, Session!.Value, VoteDate!.Value, Title!, CountryCode!.Value, CountryName!, Ballot!.Value, PublishedAt)
        : null;

    /// <summary>A row that passed row-level validation (ING-04) and awaits classification.</summary>
    public static ImportRow Staged(Guid importJobId, int rowNumber, string originalText, ImportRawFields raw, NormalizedImportRecord normalized, IEnumerable<string> normalizationNotes)
    {
        var row = Create(importJobId, rowNumber, originalText, raw, normalizationNotes);
        row.AssemblyVoteId = normalized.AssemblyVoteId;
        row.Session = normalized.Session;
        row.VoteDate = normalized.VoteDate;
        row.Title = normalized.Title;
        row.CountryCode = normalized.CountryCode;
        row.CountryName = normalized.CountryName;
        row.Ballot = normalized.Ballot;
        row.PublishedAt = normalized.PublishedAt;
        return row;
    }

    /// <summary>A row that failed structural or row-level validation: final classification Invalid, reported with reason and original value (ING-07).</summary>
    public static ImportRow Invalid(Guid importJobId, int rowNumber, string originalText, ImportRawFields raw, string reasonCode, string message, IEnumerable<string> normalizationNotes)
    {
        var row = Create(importJobId, rowNumber, originalText, raw, normalizationNotes);
        row.Classification = ImportClassification.Invalid;
        row.ReasonCode = reasonCode;
        row.Message = message;
        return row;
    }

    public void Classify(ImportClassification classification, string? reasonCode = null, string? message = null, Guid? conflictId = null)
    {
        EnsureClassifiable();
        if (classification is ImportClassification.Invalid or ImportClassification.Duplicate)
        {
            throw new InvalidOperationException($"Use {(classification == ImportClassification.Invalid ? nameof(Invalid) : nameof(ClassifyAsDuplicate))} for {classification}.");
        }

        if ((classification == ImportClassification.Conflict) != conflictId.HasValue)
        {
            throw new InvalidOperationException("A Conflict classification must reference exactly one conflict.");
        }

        Classification = classification;
        ReasonCode = reasonCode;
        Message = message;
        ConflictId = conflictId;
    }

    public void ClassifyAsDuplicate(int duplicateOfRowNumber, DuplicateKind kind, string message)
    {
        EnsureClassifiable();
        Classification = ImportClassification.Duplicate;
        DuplicateOfRowNumber = duplicateOfRowNumber;
        DuplicateKind = kind;
        ReasonCode = kind == Imports.DuplicateKind.Exact ? "duplicate.exact" : "duplicate.after_normalization";
        Message = message;
    }

    private void EnsureClassifiable()
    {
        if (Classification is not null)
        {
            throw new InvalidOperationException($"Row {RowNumber} of import {ImportJobId} is already classified as {Classification}.");
        }

        if (!IsNormalized)
        {
            throw new InvalidOperationException($"Row {RowNumber} of import {ImportJobId} has no normalized values.");
        }
    }

    private static ImportRow Create(Guid importJobId, int rowNumber, string originalText, ImportRawFields raw, IEnumerable<string> normalizationNotes) => new()
    {
        ImportJobId = importJobId,
        RowNumber = rowNumber,
        OriginalText = originalText,
        RawAssemblyVoteId = raw.AssemblyVoteId,
        RawSession = raw.Session,
        RawVoteDate = raw.VoteDate,
        RawResolutionTitle = raw.ResolutionTitle,
        RawCountryCode = raw.CountryCode,
        RawCountryName = raw.CountryName,
        RawBallot = raw.Ballot,
        RawPublishedAt = raw.PublishedAt,
        _normalizationNotes = normalizationNotes.ToList(),
    };
}
