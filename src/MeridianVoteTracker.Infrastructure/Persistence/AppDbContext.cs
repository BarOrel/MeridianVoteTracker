using MeridianVoteTracker.Application.Abstractions;
using MeridianVoteTracker.Domain.Conflicts;
using MeridianVoteTracker.Domain.History;
using MeridianVoteTracker.Domain.Imports;
using MeridianVoteTracker.Domain.MemberStates;
using MeridianVoteTracker.Domain.Users;
using MeridianVoteTracker.Domain.Votes;
using MeridianVoteTracker.Infrastructure.Persistence.Converters;
using Microsoft.EntityFrameworkCore;

namespace MeridianVoteTracker.Infrastructure.Persistence;

/// <summary>
/// EF Core context. Use cases see it only as <see cref="IApplicationDbContext"/>; the concrete type is
/// internal to Infrastructure. Two write rules are enforced at SaveChanges:
/// <list type="number">
/// <item>Writes only inside a transaction opened by <see cref="EfUnitOfWork"/> (no implicit transactions).</item>
/// <item>Only <see cref="OrdinaryWritableTypes"/> may be written freely. Approved data, conflicts, audit
/// trail and reference data can only be written inside <see cref="AllowRestrictedWrites"/>, which is
/// internal and therefore available only to Infrastructure ports (approved-data store, seeder).</item>
/// </list>
/// </summary>
internal sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options), IApplicationDbContext
{
    /// <summary>Allow-list (not deny-list): a newly added entity type is restricted until consciously opened up.</summary>
    private static readonly HashSet<Type> OrdinaryWritableTypes = [typeof(ImportJob), typeof(ImportRow), typeof(ImportVoteOutcome)];

    private int _restrictedWriteScopes;

    public DbSet<MemberState> MemberStates => Set<MemberState>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Vote> Votes => Set<Vote>();
    public DbSet<CountryVote> CountryVotes => Set<CountryVote>();
    public DbSet<Conflict> Conflicts => Set<Conflict>();
    public DbSet<VoteHistoryEvent> VoteHistoryEvents => Set<VoteHistoryEvent>();
    public DbSet<ImportJob> ImportJobs => Set<ImportJob>();
    public DbSet<ImportRow> ImportRows => Set<ImportRow>();
    public DbSet<ImportVoteOutcome> ImportVoteOutcomes => Set<ImportVoteOutcome>();

    // Read-only, untracked views for use cases: entities loaded here can never be persisted by accident.
    IQueryable<Vote> IApplicationDbContext.Votes => Votes.AsNoTracking();
    IQueryable<CountryVote> IApplicationDbContext.CountryVotes => CountryVotes.AsNoTracking();
    IQueryable<Conflict> IApplicationDbContext.Conflicts => Conflicts.AsNoTracking();
    IQueryable<VoteHistoryEvent> IApplicationDbContext.VoteHistory => VoteHistoryEvents.AsNoTracking();
    IQueryable<MemberState> IApplicationDbContext.MemberStates => MemberStates.AsNoTracking();
    IQueryable<AppUser> IApplicationDbContext.Users => Users.AsNoTracking();

    /// <summary>
    /// Opens a scope in which restricted entity types may be written. Only for Infrastructure ports
    /// whose methods enforce the domain invariants (and write the audit events) themselves.
    /// The port must flush (SaveChanges) before disposing the scope; the commit stays with the unit of work.
    /// Any restricted change still pending when the scope closes is rejected by the next SaveChanges.
    /// </summary>
    internal IDisposable AllowRestrictedWrites()
    {
        _restrictedWriteScopes++;
        return new RestrictedWriteScope(this);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<CountryCode>().HaveConversion<CountryCodeConverter>().HaveMaxLength(3).AreFixedLength();
        configurationBuilder.Properties<AssemblyVoteId>().HaveConversion<AssemblyVoteIdConverter>().HaveMaxLength(AssemblyVoteId.MaxLength);
        configurationBuilder.Properties<Weight>().HaveConversion<WeightConverter>();
        configurationBuilder.Properties<Ballot>().HaveConversion<BallotConverter>().HaveMaxLength(1).AreFixedLength();

        // Enums as readable text; every enum column also gets a CHECK constraint in its configuration.
        configurationBuilder.Properties<UserRole>().HaveConversion<string>().HaveMaxLength(16);
        configurationBuilder.Properties<Position>().HaveConversion<string>().HaveMaxLength(16);
        configurationBuilder.Properties<ApprovalOrigin>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<ConflictKind>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<ConflictField>().HaveConversion<string>().HaveMaxLength(16);
        configurationBuilder.Properties<ConflictStatus>().HaveConversion<string>().HaveMaxLength(16);
        configurationBuilder.Properties<VoteHistoryEventType>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<ImportJobStatus>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<ImportPhase>().HaveConversion<string>().HaveMaxLength(16);
        configurationBuilder.Properties<ImportClassification>().HaveConversion<string>().HaveMaxLength(16);
        configurationBuilder.Properties<DuplicateKind>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<VoteImportOutcome>().HaveConversion<string>().HaveMaxLength(32);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        EnsureInsideTransaction();
        EnsureNoUnauthorizedRestrictedWrites();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnsureInsideTransaction();
        EnsureNoUnauthorizedRestrictedWrites();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    private void EnsureNoUnauthorizedRestrictedWrites()
    {
        if (_restrictedWriteScopes > 0)
        {
            return;
        }

        // Owned types (e.g. ApprovalProvenance) are judged by their owner.
        var offending = ChangeTracker.Entries()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Select(e => (e.Metadata.IsOwned() ? e.Metadata.FindOwnership()!.PrincipalEntityType : e.Metadata).ClrType)
            .Where(type => !OrdinaryWritableTypes.Contains(type))
            .Distinct()
            .ToList();

        if (offending.Count > 0)
        {
            throw new InvalidOperationException(
                $"Writes to {string.Join(", ", offending.Select(t => t.Name))} are restricted: approved data, conflicts, " +
                "audit trail and reference data may only be changed through their dedicated persistence port.");
        }
    }

    private sealed class RestrictedWriteScope(AppDbContext db) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                db._restrictedWriteScopes--;
            }
        }
    }

    /// <summary>
    /// Forbids EF's implicit per-SaveChanges transaction: every write belongs to a boundary chosen by a use case.
    /// </summary>
    private void EnsureInsideTransaction()
    {
        if (Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "Database writes must run inside IUnitOfWork.ExecuteInTransactionAsync; implicit transactions are not allowed.");
        }
    }
}
