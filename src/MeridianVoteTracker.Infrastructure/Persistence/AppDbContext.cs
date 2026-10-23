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
/// EF Core context. Internal to Infrastructure: controllers and use cases never see it; they work
/// through focused ports. Writes are only allowed inside a transaction opened by <see cref="EfUnitOfWork"/>.
/// </summary>
internal sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<MemberState> MemberStates => Set<MemberState>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Vote> Votes => Set<Vote>();
    public DbSet<CountryVote> CountryVotes => Set<CountryVote>();
    public DbSet<Conflict> Conflicts => Set<Conflict>();
    public DbSet<VoteHistoryEvent> VoteHistoryEvents => Set<VoteHistoryEvent>();
    public DbSet<ImportJob> ImportJobs => Set<ImportJob>();
    public DbSet<ImportRow> ImportRows => Set<ImportRow>();

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
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        EnsureInsideTransaction();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnsureInsideTransaction();
        return base.SaveChanges(acceptAllChangesOnSuccess);
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
