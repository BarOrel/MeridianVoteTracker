using MeridianVoteTracker.Application.Abstractions;
using MeridianVoteTracker.Application.Common;
using MeridianVoteTracker.Application.Votes.Ports;
using MeridianVoteTracker.Domain.Common;
using MeridianVoteTracker.Domain.Conflicts;
using MeridianVoteTracker.Domain.History;
using MeridianVoteTracker.Domain.Votes;
using Microsoft.EntityFrameworkCore;

namespace MeridianVoteTracker.Application.Votes;

public sealed record VoteSummaryDto(int Id, string AssemblyVoteId, int Session, DateOnly VoteDate, string Title, int CountryVotes,
    Position? Position, int? Weight, int PendingConflicts);

public sealed record CountryVoteDto(string StateCode, string StateName, Ballot Ballot, int Revision, ApprovalOrigin Origin, string ApprovedBy, DateTimeOffset ApprovedAt);

public sealed record VoteDetailsDto(int Id, string AssemblyVoteId, int Session, DateOnly VoteDate, string Title,
    Position? Position, int? Weight, int PositionRevision, int PendingConflicts, IReadOnlyList<CountryVoteDto> CountryVotes);

public sealed record VoteHistoryEntryDto(long Id, VoteHistoryEventType Type, DateTimeOffset OccurredAt, string Actor, string? StateCode, ConflictField? Field,
    string? OldValue, string? NewValue, string? OldSourceFile, int? OldSourceRow, string? NewSourceFile, int? NewSourceRow, Guid? ConflictId, string? Comment);

public sealed record SetPositionCommand(Position Position, int Weight, int ExpectedRevision);

/// <summary>Votes list with filters (session, date range, text, without position) for the Votes screen.</summary>
public sealed class ListVotesUseCase(IApplicationDbContext db)
{
    public async Task<IReadOnlyList<VoteSummaryDto>> ExecuteAsync(int? session, DateOnly? from, DateOnly? to, bool withoutPositionOnly, CancellationToken cancellationToken)
    {
        var votes = db.Votes;
        if (session is { } s) votes = votes.Where(v => v.Session == s);
        if (from is { } f) votes = votes.Where(v => v.VoteDate >= f);
        if (to is { } t) votes = votes.Where(v => v.VoteDate <= t);
        if (withoutPositionOnly) votes = votes.Where(v => v.Position == null);

        var rows = await votes
            .OrderBy(v => v.VoteDate).ThenBy(v => v.Id)
            .Take(2000)
            .Select(v => new
            {
                v,
                Count = db.CountryVotes.Count(cv => cv.VoteId == v.Id),
                Pending = db.Conflicts.Count(c => c.VoteId == v.Id && c.Status == ConflictStatus.Pending),
            })
            .ToListAsync(cancellationToken);
        return rows.Select(x => new VoteSummaryDto(x.v.Id, x.v.AssemblyVoteId.Value, x.v.Session, x.v.VoteDate, x.v.Title, x.Count,
            x.v.Position, x.v.Weight?.Value, x.Pending)).ToList();
    }
}

public sealed class GetVoteUseCase(IApplicationDbContext db)
{
    public async Task<VoteDetailsDto> ExecuteAsync(int voteId, CancellationToken cancellationToken)
    {
        var vote = await db.Votes.SingleOrDefaultAsync(v => v.Id == voteId, cancellationToken)
            ?? throw UseCaseException.NotFound("vote.not_found", $"Vote {voteId} does not exist.");
        var names = await db.MemberStates.ToDictionaryAsync(m => m.Code, m => m.Name, cancellationToken);
        var ballots = await db.CountryVotes.Where(cv => cv.VoteId == voteId).ToListAsync(cancellationToken);
        var pending = await db.Conflicts.CountAsync(c => c.VoteId == voteId && c.Status == ConflictStatus.Pending, cancellationToken);

        return new VoteDetailsDto(vote.Id, vote.AssemblyVoteId.Value, vote.Session, vote.VoteDate, vote.Title, vote.Position, vote.Weight?.Value,
            vote.PositionRevision, pending,
            ballots.OrderBy(b => b.StateCode.Value, StringComparer.Ordinal)
                .Select(b => new CountryVoteDto(b.StateCode.Value, names.GetValueOrDefault(b.StateCode, b.StateCode.Value), b.Ballot, b.Revision,
                    b.Provenance.Origin, b.Provenance.ApprovedBy, b.Provenance.ApprovedAt))
                .ToList());
    }
}

/// <summary>HIS-04: everything that happened to a vote, newest first, with old/new values and their source files.</summary>
public sealed class GetVoteTimelineUseCase(IApplicationDbContext db)
{
    public async Task<IReadOnlyList<VoteHistoryEntryDto>> ExecuteAsync(int voteId, CancellationToken cancellationToken)
    {
        if (!await db.Votes.AnyAsync(v => v.Id == voteId, cancellationToken))
        {
            throw UseCaseException.NotFound("vote.not_found", $"Vote {voteId} does not exist.");
        }

        var events = await db.VoteHistory.Where(e => e.VoteId == voteId).OrderByDescending(e => e.OccurredAt).ThenByDescending(e => e.Id)
            .ToListAsync(cancellationToken);
        var jobIds = events.SelectMany(e => new[] { e.OldSourceImportJobId, e.NewSourceImportJobId }).OfType<Guid>().Distinct().ToList();
        var files = await db.ImportJobs.AsNoTracking().Where(j => jobIds.Contains(j.Id)).ToDictionaryAsync(j => j.Id, j => j.OriginalFileName, cancellationToken);

        string? File(Guid? id) => id is { } g ? files.GetValueOrDefault(g) : null;
        return events.Select(e => new VoteHistoryEntryDto(e.Id, e.Type, e.OccurredAt, e.Actor, e.StateCode?.Value, e.Field, e.OldValue, e.NewValue,
            File(e.OldSourceImportJobId), e.OldSourceRowNumber, File(e.NewSourceImportJobId), e.NewSourceRowNumber, e.ConflictId, e.Comment)).ToList();
    }
}

/// <summary>POS-01..03: admin sets position and weight; validation errors are reported per field; every change is in the history.</summary>
public sealed class SetVotePositionUseCase(IApprovedVoteStore approvedVotes, IUnitOfWork unitOfWork, ICurrentUser currentUser, TimeProvider time, GetVoteUseCase getVote)
{
    public async Task<VoteDetailsDto> ExecuteAsync(int voteId, SetPositionCommand command, CancellationToken cancellationToken)
    {
        if (command.Weight is < Weight.Min or > Weight.Max)
        {
            throw UseCaseException.Validation("weight", "weight.out_of_range", $"Weight must be between {Weight.Min} (minor) and {Weight.Max} (critical).");
        }

        bool found;
        try
        {
            found = await unitOfWork.ExecuteInTransactionAsync(ct => approvedVotes.SetPositionAsync(
                voteId, command.Position, Weight.Create(command.Weight), command.ExpectedRevision, currentUser.Id, time.GetUtcNow(), ct), cancellationToken);
        }
        catch (Exception ex) when (ex is DbUpdateConcurrencyException || ex is DomainException { Code: DomainErrors.PositionChanged })
        {
            throw new UseCaseException(UseCaseErrorKind.Conflict, DomainErrors.PositionChanged, "The position was changed by someone else; reload and try again.")
            {
                CurrentState = await getVote.ExecuteAsync(voteId, cancellationToken),
            };
        }

        return found
            ? await getVote.ExecuteAsync(voteId, cancellationToken)
            : throw UseCaseException.NotFound("vote.not_found", $"Vote {voteId} does not exist.");
    }
}
