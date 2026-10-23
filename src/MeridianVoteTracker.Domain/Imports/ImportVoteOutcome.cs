using MeridianVoteTracker.Domain.Votes;

namespace MeridianVoteTracker.Domain.Imports;

public enum VoteImportOutcome
{
    /// <summary>The vote did not exist; it was created with the file's valid rows.</summary>
    NewVote,

    /// <summary>The vote existed; states missing from it were added (ING-10).</summary>
    CountriesAdded,

    /// <summary>At least one difference from approved data went to review; approved data unchanged.</summary>
    ConflictsDetected,

    /// <summary>Everything matched approved data (or was a duplicate).</summary>
    NoChange,

    /// <summary>The rows disagree on the vote's own details; nothing of this vote was stored (ING-05).</summary>
    BlockedByContradiction,
}

/// <summary>
/// "What happened to each vote" in one import (ING-14). Written once, in the same transaction as the
/// vote's row classifications; immutable afterwards (database trigger).
/// </summary>
public sealed class ImportVoteOutcome
{
    private List<string> _notes = [];

    public Guid ImportJobId { get; private set; }
    public AssemblyVoteId AssemblyVoteId { get; private set; }
    public int? VoteId { get; private set; }
    public VoteImportOutcome Outcome { get; private set; }
    public int NewRows { get; private set; }
    public int UnchangedRows { get; private set; }
    public int DuplicateRows { get; private set; }
    public int ContradictionRows { get; private set; }
    public int ConflictRows { get; private set; }
    public int ConflictsCreated { get; private set; }
    public IReadOnlyList<string> Notes => _notes;

    private ImportVoteOutcome() { }

    internal ImportVoteOutcome(Guid importJobId, AssemblyVoteId assemblyVoteId, int? voteId, VoteImportOutcome outcome,
        int newRows, int unchangedRows, int duplicateRows, int contradictionRows, int conflictRows, int conflictsCreated, IEnumerable<string> notes)
    {
        ImportJobId = importJobId;
        AssemblyVoteId = assemblyVoteId;
        VoteId = voteId;
        Outcome = outcome;
        NewRows = newRows;
        UnchangedRows = unchangedRows;
        DuplicateRows = duplicateRows;
        ContradictionRows = contradictionRows;
        ConflictRows = conflictRows;
        ConflictsCreated = conflictsCreated;
        _notes = notes.ToList();
    }

    /// <summary>A vote created by this import gets its database id only when it is inserted.</summary>
    public void LinkCreatedVote(int voteId)
    {
        if (Outcome != VoteImportOutcome.NewVote || VoteId is not null)
        {
            throw new InvalidOperationException($"Outcome for {AssemblyVoteId} is not an unlinked new vote.");
        }

        VoteId = voteId;
    }
}
