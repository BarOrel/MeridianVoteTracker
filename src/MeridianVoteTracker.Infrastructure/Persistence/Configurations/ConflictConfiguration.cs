using MeridianVoteTracker.Domain.Conflicts;
using MeridianVoteTracker.Domain.Imports;
using MeridianVoteTracker.Domain.MemberStates;
using MeridianVoteTracker.Domain.Votes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeridianVoteTracker.Infrastructure.Persistence.Configurations;

internal sealed class ConflictConfiguration : IEntityTypeConfiguration<Conflict>
{
    public void Configure(EntityTypeBuilder<Conflict> builder)
    {
        builder.ToTable("conflicts", t =>
        {
            t.HasCheckConstraint("ck_conflicts_shape",
                "(kind = 'CountryBallot' AND state_code IS NOT NULL AND field = 'Ballot') OR " +
                "(kind = 'VoteMetadata' AND state_code IS NULL AND field IN ('Session', 'VoteDate', 'Title'))");
            t.HasCheckConstraint("ck_conflicts_status", "status IN ('Pending', 'Accepted', 'Rejected', 'Superseded')");
            t.HasCheckConstraint("ck_conflicts_resolution", "(status = 'Pending') = (resolved_at IS NULL AND resolved_by IS NULL)");
            t.HasCheckConstraint("ck_conflicts_has_difference", "approved_value_at_detection <> proposed_value");
        });
        builder.HasKey(c => c.Id);

        // Conditional update: UPDATE conflicts ... WHERE id = @id AND status = 'Pending'.
        // Two analysts deciding the same conflict: exactly one UPDATE matches (CON-05).
        builder.Property(c => c.Status).IsConcurrencyToken();
        builder.Property(c => c.ApprovedValueAtDetection).HasMaxLength(512);
        builder.Property(c => c.ProposedValue).HasMaxLength(512);
        builder.Property(c => c.ResolvedBy).HasMaxLength(64);
        builder.Property(c => c.ResolutionComment).HasMaxLength(2000);

        builder.HasOne<Vote>().WithMany().HasForeignKey(c => c.VoteId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<MemberState>().WithMany().HasForeignKey(c => c.StateCode).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ImportJob>().WithMany().HasForeignKey(c => c.SourceImportJobId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Conflict>().WithMany().HasForeignKey(c => c.SupersededByConflictId).OnDelete(DeleteBehavior.Restrict);

        // ING-12: at most one pending conflict per fact and proposed value, even under concurrent imports.
        builder.HasIndex(c => new { c.VoteId, c.StateCode, c.ProposedValue })
            .IsUnique()
            .HasFilter("status = 'Pending' AND kind = 'CountryBallot'")
            .HasDatabaseName("ux_conflicts_pending_ballot");
        builder.HasIndex(c => new { c.VoteId, c.Field, c.ProposedValue })
            .IsUnique()
            .HasFilter("status = 'Pending' AND kind = 'VoteMetadata'")
            .HasDatabaseName("ux_conflicts_pending_metadata");
        builder.HasIndex(c => new { c.Status, c.VoteId });
    }
}
