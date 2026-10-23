using MeridianVoteTracker.Domain.Conflicts;
using MeridianVoteTracker.Domain.History;
using MeridianVoteTracker.Domain.Imports;
using MeridianVoteTracker.Domain.MemberStates;
using MeridianVoteTracker.Domain.Users;
using MeridianVoteTracker.Domain.Votes;
using Microsoft.EntityFrameworkCore;

namespace MeridianVoteTracker.Application.Abstractions;

/// <summary>
/// The persistence surface for ordinary use cases: LINQ queries and simple writes, without a
/// repository per entity. Two tiers:
/// <list type="bullet">
/// <item><b>Writable</b> (<see cref="DbSet{TEntity}"/>): workflow data use cases create and update
/// directly: import jobs and import rows.</item>
/// <item><b>Read-only</b> (<see cref="IQueryable{T}"/>, not tracked): approved data, conflicts, audit
/// trail and reference data. A use case cannot add, remove or persist changes to these through this
/// interface. Changes go through specialized ports (e.g. IApprovedVoteStore) that enforce the
/// approved-data invariants atomically; the context additionally rejects such writes at SaveChanges.</item>
/// </list>
/// There is no SaveChanges here: changes are committed by <see cref="IUnitOfWork"/> at the end of
/// the use case's transaction boundary.
/// </summary>
public interface IApplicationDbContext
{
    DbSet<ImportJob> ImportJobs { get; }

    DbSet<ImportRow> ImportRows { get; }

    IQueryable<Vote> Votes { get; }

    IQueryable<CountryVote> CountryVotes { get; }

    IQueryable<Conflict> Conflicts { get; }

    IQueryable<VoteHistoryEvent> VoteHistory { get; }

    IQueryable<MemberState> MemberStates { get; }

    IQueryable<AppUser> Users { get; }
}
