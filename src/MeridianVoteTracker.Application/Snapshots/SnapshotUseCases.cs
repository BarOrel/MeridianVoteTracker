using System.Text.RegularExpressions;
using MeridianVoteTracker.Application.Abstractions;
using MeridianVoteTracker.Application.Common;
using MeridianVoteTracker.Application.Snapshots.Ports;
using MeridianVoteTracker.Domain.Snapshots;
using MeridianVoteTracker.Domain.Votes;
using Microsoft.EntityFrameworkCore;

namespace MeridianVoteTracker.Application.Snapshots;

public sealed record CreateSnapshotCommand(string Name, DateOnly? From, DateOnly? To, int? Session, IReadOnlyList<string>? AssemblyVoteIds);

public sealed record SnapshotSummaryDto(Guid Id, string Name, string Selection, int Votes, SnapshotStatus Status, string CreatedBy, DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt, string? Error, string? PublishedQuarter);

public sealed record SnapshotRankingDto(int? Rank, string StateCode, string StateName, decimal? Score, int CountedWeight, int MatchingWeight);

public sealed record SnapshotDto(SnapshotSummaryDto Snapshot, IReadOnlyList<string> Rules, string? PublishedBy, DateTimeOffset? PublishedAt, IReadOnlyList<SnapshotRankingDto> Ranking);

public sealed record SnapshotContributionDto(string AssemblyVoteId, string Title, DateOnly VoteDate, Position Position, int Weight, string Ballot, bool Counted, bool Matching);

public sealed record SnapshotCountryDto(string StateCode, string StateName, decimal? Score, int? Rank, int CountedWeight, int MatchingWeight, IReadOnlyList<SnapshotContributionDto> Votes);

/// <summary>
/// SNP-01/02, POS-04: validates the selection (votes exist; every one has a position) and queues the snapshot.
/// The calculation runs in the background; the response returns immediately with status Pending.
/// </summary>
public sealed class CreateSnapshotUseCase(IApplicationDbContext db, IUnitOfWork unitOfWork, ICurrentUser currentUser, TimeProvider time, GetSnapshotUseCase getSnapshot)
{
    public async Task<SnapshotDto> ExecuteAsync(CreateSnapshotCommand command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.Name) || command.Name.Trim().Length > Snapshot.MaxNameLength)
        {
            throw UseCaseException.Validation("name", "snapshot.name_invalid", $"A name of 1 to {Snapshot.MaxNameLength} characters is required.");
        }

        var votes = db.Votes;
        var parts = new List<string>();
        if (command.From is { } from) { votes = votes.Where(v => v.VoteDate >= from); parts.Add($"from {Vote.FormatDate(from)}"); }
        if (command.To is { } to) { votes = votes.Where(v => v.VoteDate <= to); parts.Add($"to {Vote.FormatDate(to)}"); }
        if (command.Session is { } session) { votes = votes.Where(v => v.Session == session); parts.Add($"session {session}"); }
        if (command.AssemblyVoteIds is { Count: > 0 } ids)
        {
            var parsed = ids.Select(i => AssemblyVoteId.TryCreate(i.Trim(), out var id) ? id : throw UseCaseException.Validation("assemblyVoteIds", "vote_id.invalid", $"'{i}' is not a vote id.")).ToList();
            votes = votes.Where(v => parsed.Contains(v.AssemblyVoteId));
            parts.Add($"{parsed.Count} listed votes");
        }

        if (parts.Count == 0)
        {
            throw UseCaseException.Validation("selection", "snapshot.selection_required", "Choose a date range, a session and/or a list of votes.");
        }

        var selected = await votes.Select(v => new { v.Id, v.AssemblyVoteId, v.Position }).ToListAsync(cancellationToken);
        if (selected.Count == 0)
        {
            throw UseCaseException.Validation("selection", "snapshot.no_votes", "No votes match this selection.");
        }

        var withoutPosition = selected.Where(v => v.Position is null).Select(v => v.AssemblyVoteId.Value).Order(StringComparer.Ordinal).ToList();
        if (withoutPosition.Count > 0)
        {
            throw UseCaseException.Validation("selection", "snapshot.votes_without_position",
                $"{withoutPosition.Count} selected vote(s) have no position: {string.Join(", ", withoutPosition.Take(20))}{(withoutPosition.Count > 20 ? ", …" : "")}. " +
                "Set a position or remove them from the selection.");
        }

        var snapshot = Snapshot.Create(command.Name, string.Join(", ", parts), selected.Select(v => v.Id).ToList(), currentUser.Id, time.GetUtcNow());
        await unitOfWork.ExecuteInTransactionAsync(_ =>
        {
            db.Snapshots.Add(snapshot);
            return Task.CompletedTask;
        }, cancellationToken);
        return await getSnapshot.ExecuteAsync(snapshot.Id, cancellationToken);
    }
}

public sealed class ListSnapshotsUseCase(IApplicationDbContext db)
{
    public async Task<IReadOnlyList<SnapshotSummaryDto>> ExecuteAsync(CancellationToken cancellationToken)
    {
        var snapshots = await db.Snapshots.AsNoTracking().OrderByDescending(s => s.CreatedAt).Take(200).ToListAsync(cancellationToken);
        var published = await db.SnapshotPublications.AsNoTracking().ToDictionaryAsync(p => p.SnapshotId, p => p.Quarter, cancellationToken);
        return snapshots.Select(s => SnapshotMapping.Summary(s, published.GetValueOrDefault(s.Id))).ToList();
    }
}

/// <summary>SNP-03/04: the frozen ranking and the rules used; reads only snapshot tables, never live approved data.</summary>
public sealed class GetSnapshotUseCase(IApplicationDbContext db)
{
    public async Task<SnapshotDto> ExecuteAsync(Guid snapshotId, CancellationToken cancellationToken)
    {
        var snapshot = await db.Snapshots.AsNoTracking().SingleOrDefaultAsync(s => s.Id == snapshotId, cancellationToken)
            ?? throw UseCaseException.NotFound("snapshot.not_found", $"Snapshot {snapshotId} does not exist.");
        var publication = await db.SnapshotPublications.AsNoTracking().SingleOrDefaultAsync(p => p.SnapshotId == snapshotId, cancellationToken);
        var names = await db.MemberStates.ToDictionaryAsync(m => m.Code.Value, m => m.Name, cancellationToken);
        var results = await db.SnapshotResults.Where(r => r.SnapshotId == snapshotId).ToListAsync(cancellationToken);

        var ranking = results
            .OrderBy(r => r.Rank ?? int.MaxValue).ThenBy(r => names.GetValueOrDefault(r.StateCode, r.StateCode), StringComparer.Ordinal)
            .Select(r => new SnapshotRankingDto(r.Rank, r.StateCode, names.GetValueOrDefault(r.StateCode, r.StateCode), r.Score, r.CountedWeight, r.MatchingWeight))
            .ToList();
        return new SnapshotDto(SnapshotMapping.Summary(snapshot, publication?.Quarter), Snapshot.Rules, publication?.PublishedBy, publication?.PublishedAt, ranking);
    }
}

/// <summary>SNP-03: how one state's score was computed, vote by vote.</summary>
public sealed class GetSnapshotCountryUseCase(IApplicationDbContext db)
{
    public async Task<SnapshotCountryDto> ExecuteAsync(Guid snapshotId, string stateCode, CancellationToken cancellationToken)
    {
        var code = stateCode.Trim().ToUpperInvariant();
        var result = await db.SnapshotResults.SingleOrDefaultAsync(r => r.SnapshotId == snapshotId && r.StateCode == code, cancellationToken)
            ?? throw UseCaseException.NotFound("snapshot.country_not_found", $"No result for {code} in snapshot {snapshotId}.");
        var countryCode = Domain.MemberStates.CountryCode.Create(code);
        var name = await db.MemberStates.Where(m => m.Code == countryCode).Select(m => m.Name).SingleOrDefaultAsync(cancellationToken) ?? code;
        var votes = await db.SnapshotEntries.Where(e => e.SnapshotId == snapshotId && e.StateCode == code)
            .Join(db.SnapshotVotes.Where(v => v.SnapshotId == snapshotId), e => e.VoteId, v => v.VoteId, (e, v) => new { e, v })
            .OrderBy(x => x.v.VoteDate).ThenBy(x => x.v.AssemblyVoteId)
            .Select(x => new SnapshotContributionDto(x.v.AssemblyVoteId, x.v.Title, x.v.VoteDate, x.v.Position, x.v.Weight, x.e.Ballot, x.e.Counted, x.e.Matching))
            .ToListAsync(cancellationToken);
        return new SnapshotCountryDto(code, name, result.Score, result.Rank, result.CountedWeight, result.MatchingWeight, votes);
    }
}

/// <summary>SNP-05: an admin marks one completed snapshot as published for a quarter; a quarter is published once.</summary>
public sealed partial class PublishSnapshotUseCase(IApplicationDbContext db, IUnitOfWork unitOfWork, ICurrentUser currentUser, TimeProvider time, GetSnapshotUseCase getSnapshot)
{
    public async Task<SnapshotDto> ExecuteAsync(Guid snapshotId, string quarter, CancellationToken cancellationToken)
    {
        quarter = (quarter ?? string.Empty).Trim().ToUpperInvariant();
        if (!QuarterFormat().IsMatch(quarter))
        {
            throw UseCaseException.Validation("quarter", "quarter.invalid", "Quarter must look like 2024-Q1.");
        }

        var snapshot = await getSnapshot.ExecuteAsync(snapshotId, cancellationToken);
        if (snapshot.Snapshot.Status != SnapshotStatus.Completed)
        {
            throw new UseCaseException(UseCaseErrorKind.Conflict, "snapshot.not_completed", "Only a completed snapshot can be published.") { CurrentState = snapshot };
        }

        var existing = await db.SnapshotPublications.AsNoTracking().FirstOrDefaultAsync(p => p.Quarter == quarter || p.SnapshotId == snapshotId, cancellationToken);
        if (existing is not null)
        {
            throw new UseCaseException(UseCaseErrorKind.Conflict, "snapshot.already_published",
                existing.SnapshotId == snapshotId
                    ? $"This snapshot is already published for {existing.Quarter}."
                    : $"{quarter} already has a published snapshot (published by {existing.PublishedBy}).") { CurrentState = snapshot };
        }

        try
        {
            await unitOfWork.ExecuteInTransactionAsync(_ =>
            {
                db.SnapshotPublications.Add(new SnapshotPublication(quarter, snapshotId, currentUser.Id, time.GetUtcNow()));
                return Task.CompletedTask;
            }, cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Two admins publishing the same quarter at once: the primary key on the quarter lets only one win.
            throw new UseCaseException(UseCaseErrorKind.Conflict, "snapshot.already_published", $"{quarter} was just published by someone else.");
        }

        return await getSnapshot.ExecuteAsync(snapshotId, cancellationToken);
    }

    [GeneratedRegex(@"^\d{4}-Q[1-4]$")]
    private static partial Regex QuarterFormat();
}

/// <summary>Background step (SNP-02): calculate the oldest pending snapshot in one transaction; a crash leaves it pending and it is retried.</summary>
public sealed class CalculateNextSnapshotUseCase(IApplicationDbContext db, IUnitOfWork unitOfWork, ISnapshotCalculator calculator, TimeProvider time)
{
    public async Task<bool> ExecuteAsync(CancellationToken cancellationToken)
    {
        Guid? claimed = null;
        try
        {
            return await unitOfWork.ExecuteInTransactionAsync(async ct =>
            {
                claimed = await calculator.LockNextPendingAsync(ct);
                if (claimed is null)
                {
                    return false;
                }

                var snapshot = await db.Snapshots.SingleAsync(s => s.Id == claimed, ct);
                await calculator.CalculateAsync(snapshot.Id, snapshot.VoteIds, ct);
                snapshot.Complete(time.GetUtcNow());
                return true;
            }, cancellationToken);
        }
        catch (Exception ex) when (claimed is not null && ex is not OperationCanceledException)
        {
            await unitOfWork.ExecuteInTransactionAsync(async ct =>
            {
                var snapshot = await db.Snapshots.SingleAsync(s => s.Id == claimed, ct);
                snapshot.Fail(ex.Message, time.GetUtcNow());
            }, cancellationToken);
            return true;
        }
    }
}

internal static class SnapshotMapping
{
    public static SnapshotSummaryDto Summary(Snapshot s, string? quarter) =>
        new(s.Id, s.Name, s.Selection, s.VoteIds.Count, s.Status, s.CreatedBy, s.CreatedAt, s.CompletedAt, s.Error, quarter);
}
