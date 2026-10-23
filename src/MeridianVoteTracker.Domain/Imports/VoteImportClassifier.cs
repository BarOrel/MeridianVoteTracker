using MeridianVoteTracker.Domain.Conflicts;
using MeridianVoteTracker.Domain.MemberStates;
using MeridianVoteTracker.Domain.Votes;

namespace MeridianVoteTracker.Domain.Imports;

/// <summary>The approved state of one vote as read inside the classification transaction.</summary>
public sealed record ApprovedVoteState(Vote Vote, IReadOnlyDictionary<CountryCode, CountryVote> CountryVotes);

public sealed record NewVoteSpec(int Session, DateOnly VoteDate, string Title, int FirstRowNumber);

public sealed record NewBallotSpec(CountryCode Country, Ballot Ballot, int RowNumber);

/// <summary>
/// Decisions for one vote. Contains only additions: a new vote, new country ballots and new conflicts.
/// Nothing in here can modify an existing approved value (ING-08).
/// </summary>
public sealed record VoteClassificationResult(
    AssemblyVoteId AssemblyVoteId,
    int? ExistingVoteId,
    NewVoteSpec? NewVote,
    IReadOnlyList<NewBallotSpec> NewBallots,
    IReadOnlyList<Conflict> NewConflicts,
    ImportVoteOutcome Outcome);

/// <summary>
/// Classifies all staged rows of one vote from one file against approved data (ING-05, ING-06, ING-08..12).
/// Pure and deterministic: rows are processed in file order, so "first occurrence wins" is stable.
/// Each row receives exactly one classification; Invalid rows were already classified during staging.
/// <list type="number">
/// <item>Duplicate: identical raw fields (Exact), or identical business facts after normalization (AfterNormalization).</item>
/// <item>Contradiction (vote): remaining rows disagree on session, date or title; nothing of the vote is stored.</item>
/// <item>Contradiction (country): one state appears with different ballots; none of them is stored.</item>
/// <item>New: vote or state ballot does not exist yet. Unchanged: equals approved. Conflict: differs from approved;
/// linked to an existing pending conflict, or to a rejection of the same proposal against the same approved
/// revision, otherwise a new conflict is opened.</item>
/// <item>Vote details that differ from the approved vote open metadata conflicts (CON-03).</item>
/// </list>
/// </summary>
public sealed class VoteImportClassifier(MembershipRegistry membership)
{
    public VoteClassificationResult Classify(
        Guid importJobId,
        AssemblyVoteId voteId,
        IReadOnlyList<ImportRow> rows,
        ApprovedVoteState? approved,
        IReadOnlyCollection<Conflict> knownConflicts,
        DateTimeOffset now)
    {
        var ordered = rows.OrderBy(r => r.RowNumber).ToList();
        if (ordered.Any(r => r.Classification is not null || r.AssemblyVoteId != voteId))
        {
            throw new ArgumentException($"Only unclassified rows of {voteId} can be classified.", nameof(rows));
        }

        var notes = new List<string>();
        var remaining = ClassifyDuplicates(ordered);

        var details = remaining.Select(r => (r.Session!.Value, r.VoteDate!.Value, r.Title!)).Distinct().ToList();
        if (details.Count > 1)
        {
            var variants = string.Join(" | ", details.Select(d => $"session {d.Item1}, {Vote.FormatDate(d.Item2)}, \"{d.Item3}\""));
            foreach (var row in remaining)
            {
                row.Classify(ImportClassification.Contradiction, "contradiction.vote_details",
                    $"Rows of {voteId} disagree on the vote details ({variants}); no value of this vote was stored from this file.");
            }

            notes.Add($"Vote details differ between rows: {variants}");
            return Result(importJobId, voteId, approved, null, [], [], ordered, VoteImportOutcome.BlockedByContradiction, notes);
        }

        var accepted = ClassifyCountryContradictions(voteId, remaining);
        AddNameVariantNotes(accepted, notes);
        if (details.Count == 0)
        {
            return Result(importJobId, voteId, approved, null, [], [], ordered, VoteImportOutcome.NoChange, notes);
        }

        var (session, voteDate, title) = details[0];
        if (approved is null)
        {
            return ClassifyNewVote(importJobId, voteId, session, voteDate, title, accepted, ordered, notes);
        }

        var newConflicts = new List<Conflict>();
        var newBallots = new List<NewBallotSpec>();
        var firstRow = accepted.Count > 0 ? accepted.Min(r => r.RowNumber) : ordered.Min(r => r.RowNumber);
        var metadataDifferences = DetectMetadataConflicts(importJobId, approved.Vote, (session, voteDate, title), firstRow, knownConflicts, newConflicts, notes, now);

        foreach (var row in accepted)
        {
            var code = row.CountryCode!.Value;
            var ballot = row.Ballot!.Value;
            if (!approved.CountryVotes.TryGetValue(code, out var current))
            {
                row.Classify(ImportClassification.New, "new.country_vote", $"Adds {code} to the existing vote {voteId} (ING-10).");
                newBallots.Add(new NewBallotSpec(code, ballot, row.RowNumber));
            }
            else if (current.Ballot == ballot)
            {
                row.Classify(ImportClassification.Unchanged, "unchanged", $"Same as the approved value ({ballot.ToCode()}).");
            }
            else
            {
                var (conflict, reason) = FindOrOpenBallotConflict(importJobId, current, ballot, row.RowNumber, knownConflicts, newConflicts, now);
                row.Classify(ImportClassification.Conflict, reason,
                    $"{code}: approved {current.Ballot.ToCode()}, this file says {ballot.ToCode()}. {Describe(reason)}", conflict.Id);
            }
        }

        var outcome = metadataDifferences > 0 || accepted.Any(r => r.Classification == ImportClassification.Conflict)
            ? VoteImportOutcome.ConflictsDetected
            : newBallots.Count > 0 ? VoteImportOutcome.CountriesAdded : VoteImportOutcome.NoChange;
        return Result(importJobId, voteId, approved, null, newBallots, newConflicts, ordered, outcome, notes);
    }

    /// <returns>Rows that are not duplicates, in file order.</returns>
    private static List<ImportRow> ClassifyDuplicates(List<ImportRow> ordered)
    {
        var byRaw = new Dictionary<ImportRawFields, ImportRow>();
        var byFacts = new Dictionary<(int, DateOnly, string, CountryCode, Ballot), ImportRow>();
        var remaining = new List<ImportRow>();
        foreach (var row in ordered)
        {
            var facts = (row.Session!.Value, row.VoteDate!.Value, row.Title!, row.CountryCode!.Value, row.Ballot!.Value);
            if (byRaw.TryGetValue(row.Raw, out var original))
            {
                row.ClassifyAsDuplicate(original.RowNumber, DuplicateKind.Exact, $"Identical to row {original.RowNumber}; ignored.");
            }
            else if (byFacts.TryGetValue(facts, out original))
            {
                row.ClassifyAsDuplicate(original.RowNumber, DuplicateKind.AfterNormalization,
                    $"Same as row {original.RowNumber} after normalization (raw text differs); ignored.");
            }
            else
            {
                byRaw.Add(row.Raw, row);
                byFacts.Add(facts, row);
                remaining.Add(row);
            }
        }

        return remaining;
    }

    /// <returns>Exactly one row per state whose ballot is unambiguous.</returns>
    private static List<ImportRow> ClassifyCountryContradictions(AssemblyVoteId voteId, List<ImportRow> remaining)
    {
        var accepted = new List<ImportRow>();
        foreach (var group in remaining.GroupBy(r => r.CountryCode!.Value))
        {
            if (group.Count() == 1)
            {
                accepted.Add(group.First());
                continue;
            }

            var description = string.Join(", ", group.Select(r => $"row {r.RowNumber}: {r.Ballot!.Value.ToCode()}"));
            foreach (var row in group)
            {
                row.Classify(ImportClassification.Contradiction, "contradiction.ballot",
                    $"{group.Key} appears more than once in {voteId} with different ballots ({description}); none was stored.");
            }
        }

        return accepted;
    }

    private VoteClassificationResult ClassifyNewVote(Guid importJobId, AssemblyVoteId voteId, int session, DateOnly voteDate, string title,
        List<ImportRow> accepted, List<ImportRow> ordered, List<string> notes)
    {
        var ballots = new List<NewBallotSpec>();
        foreach (var row in accepted)
        {
            row.Classify(ImportClassification.New, "new.vote", "New vote: stored as approved data.");
            ballots.Add(new NewBallotSpec(row.CountryCode!.Value, row.Ballot!.Value, row.RowNumber));
        }

        // A vote whose every state was contradictory still gets created: its details are unambiguous.
        var spec = new NewVoteSpec(session, voteDate, title, ordered.Min(r => r.RowNumber));
        return Result(importJobId, voteId, null, spec, ballots, [], ordered, VoteImportOutcome.NewVote, notes);
    }

    /// <returns>Number of vote details that differ from the approved vote.</returns>
    private static int DetectMetadataConflicts(Guid importJobId, Vote vote, (int Session, DateOnly Date, string Title) file, int firstRow,
        IReadOnlyCollection<Conflict> knownConflicts, List<Conflict> newConflicts, List<string> notes, DateTimeOffset now)
    {
        var proposals = new (ConflictField Field, string Value)[]
        {
            (ConflictField.Session, Vote.FormatSession(file.Session)),
            (ConflictField.VoteDate, Vote.FormatDate(file.Date)),
            (ConflictField.Title, file.Title),
        };

        var differences = 0;
        foreach (var (field, proposed) in proposals)
        {
            var approvedValue = vote.GetMetadataValue(field);
            if (approvedValue == proposed)
            {
                continue;
            }

            differences++;

            var existing = FindKnown(knownConflicts, c => c.Kind == ConflictKind.VoteMetadata && c.Field == field,
                proposed, vote.MetadataRevision, approvedValue);
            string reason;
            if (existing is not null)
            {
                reason = existing.Status == ConflictStatus.Pending ? "conflict.already_pending" : "conflict.previously_rejected";
            }
            else
            {
                newConflicts.Add(Conflict.ForVoteMetadata(vote, field, proposed, importJobId, firstRow, now));
                reason = "conflict.created";
            }

            notes.Add($"Conflict on {field}: approved \"{approvedValue}\", this file says \"{proposed}\". {Describe(reason)}");
        }

        return differences;
    }

    private static (Conflict Conflict, string Reason) FindOrOpenBallotConflict(Guid importJobId, CountryVote current, Ballot proposed, int rowNumber,
        IReadOnlyCollection<Conflict> knownConflicts, List<Conflict> newConflicts, DateTimeOffset now)
    {
        var existing = FindKnown(knownConflicts, c => c.Kind == ConflictKind.CountryBallot && c.StateCode == current.StateCode,
            proposed.ToCode(), current.Revision, current.Ballot.ToCode());
        if (existing is not null)
        {
            return (existing, existing.Status == ConflictStatus.Pending ? "conflict.already_pending" : "conflict.previously_rejected");
        }

        var conflict = Conflict.ForCountryBallot(current, proposed, importJobId, rowNumber, now);
        newConflicts.Add(conflict);
        return (conflict, "conflict.created");
    }

    /// <summary>
    /// ING-12: a pending conflict with the same proposal is reused; a proposal already rejected against the very
    /// same approved value and revision is not reopened. Anything else (e.g. approved value changed since) is new.
    /// </summary>
    private static Conflict? FindKnown(IReadOnlyCollection<Conflict> known, Func<Conflict, bool> sameTarget, string proposed, int revision, string approvedValue) =>
        known.FirstOrDefault(c => sameTarget(c) && c.ProposedValue == proposed && c.Status == ConflictStatus.Pending)
        ?? known.FirstOrDefault(c => sameTarget(c) && c.ProposedValue == proposed && c.Status == ConflictStatus.Rejected
            && c.ExpectedRevision == revision && c.ApprovedValueAtDetection == approvedValue);

    private static string Describe(string reason) => reason switch
    {
        "conflict.created" => "Sent to review; approved value unchanged.",
        "conflict.already_pending" => "Already waiting for a decision; not opened again.",
        "conflict.previously_rejected" => "This change was already rejected; not opened again.",
        _ => string.Empty,
    };

    private void AddNameVariantNotes(List<ImportRow> rows, List<string> notes)
    {
        foreach (var row in rows)
        {
            var reference = membership.NameOf(row.CountryCode!.Value);
            if (!string.IsNullOrEmpty(row.CountryName) && reference is not null && row.CountryName != reference)
            {
                notes.Add($"{row.CountryCode} is published as \"{row.CountryName}\" (reference name \"{reference}\"); the code is the identity, not a conflict (ING-11).");
            }
        }
    }

    private static VoteClassificationResult Result(Guid importJobId, AssemblyVoteId voteId, ApprovedVoteState? approved, NewVoteSpec? newVote,
        IReadOnlyList<NewBallotSpec> ballots, IReadOnlyList<Conflict> conflicts, List<ImportRow> rows, VoteImportOutcome outcome, List<string> notes)
    {
        int Count(ImportClassification c) => rows.Count(r => r.Classification == c);
        var outcomeRecord = new ImportVoteOutcome(importJobId, voteId, approved?.Vote.Id, outcome,
            Count(ImportClassification.New), Count(ImportClassification.Unchanged), Count(ImportClassification.Duplicate),
            Count(ImportClassification.Contradiction), Count(ImportClassification.Conflict), conflicts.Count, notes);
        return new VoteClassificationResult(voteId, approved?.Vote.Id, newVote, ballots, conflicts, outcomeRecord);
    }
}
