using MeridianVoteTracker.Domain.History;
using MeridianVoteTracker.Domain.Imports;
using MeridianVoteTracker.Domain.MemberStates;
using MeridianVoteTracker.Domain.Votes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeridianVoteTracker.Infrastructure.Persistence.Configurations;

internal sealed class VoteConfiguration : IEntityTypeConfiguration<Vote>
{
    public void Configure(EntityTypeBuilder<Vote> builder)
    {
        builder.ToTable("votes", t =>
        {
            t.HasCheckConstraint("ck_votes_session", "session > 0");
            t.HasCheckConstraint("ck_votes_position", "position IS NULL OR position IN ('For', 'Against')");
            t.HasCheckConstraint("ck_votes_weight", "weight IS NULL OR weight BETWEEN 1 AND 5");
            t.HasCheckConstraint("ck_votes_position_weight_together", "(position IS NULL) = (weight IS NULL)");
            t.HasCheckConstraint("ck_votes_revisions", "metadata_revision >= 1 AND position_revision >= 0");
            t.HasCheckConstraint("ck_votes_metadata_origin", "metadata_origin IN ('InitialImport', 'ConflictResolution')");
        });
        builder.HasKey(v => v.Id);
        builder.Property(v => v.Id).UseIdentityAlwaysColumn();
        builder.HasIndex(v => v.AssemblyVoteId).IsUnique();
        builder.HasIndex(v => v.VoteDate);
        builder.Property(v => v.Title).HasMaxLength(512);

        // Both counters are concurrency tokens: an UPDATE only succeeds if neither changed since the
        // entity was read. Conservative by design: a concurrent change never gets lost; at worst a retry.
        builder.Property(v => v.MetadataRevision).IsConcurrencyToken();
        builder.Property(v => v.PositionRevision).IsConcurrencyToken();

        builder.OwnsOne(v => v.MetadataProvenance, p => ProvenanceMapping.Configure(p, "metadata"));
        builder.Navigation(v => v.MetadataProvenance).IsRequired();
    }
}

internal sealed class CountryVoteConfiguration : IEntityTypeConfiguration<CountryVote>
{
    public void Configure(EntityTypeBuilder<CountryVote> builder)
    {
        builder.ToTable("country_votes", t =>
        {
            t.HasCheckConstraint("ck_country_votes_ballot", "ballot IN ('Y', 'N', 'A', 'X')");
            t.HasCheckConstraint("ck_country_votes_revision", "revision >= 1");
            t.HasCheckConstraint("ck_country_votes_origin", "approval_origin IN ('InitialImport', 'ConflictResolution')");
        });
        builder.HasKey(cv => new { cv.VoteId, cv.StateCode });
        builder.HasOne<Vote>().WithMany().HasForeignKey(cv => cv.VoteId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<MemberState>().WithMany().HasForeignKey(cv => cv.StateCode).OnDelete(DeleteBehavior.Restrict);

        // Conditional update: UPDATE ... WHERE vote_id = @v AND state_code = @c AND revision = @expected.
        builder.Property(cv => cv.Revision).IsConcurrencyToken();

        builder.OwnsOne(cv => cv.Provenance, p => ProvenanceMapping.Configure(p, "approval"));
        builder.Navigation(cv => cv.Provenance).IsRequired();
    }
}

internal static class ProvenanceMapping
{
    public static void Configure<TOwner>(OwnedNavigationBuilder<TOwner, ApprovalProvenance> p, string prefix)
        where TOwner : class
    {
        p.Property(x => x.Origin).HasColumnName($"{prefix}_origin");
        p.Property(x => x.SourceImportJobId).HasColumnName($"{prefix}_source_import_job_id");
        p.Property(x => x.SourceRowNumber).HasColumnName($"{prefix}_source_row_number");
        p.Property(x => x.ConflictId).HasColumnName($"{prefix}_conflict_id");
        p.Property(x => x.ApprovedBy).HasColumnName($"{prefix}_approved_by").HasMaxLength(64);
        p.Property(x => x.ApprovedAt).HasColumnName($"{prefix}_approved_at");
        p.HasOne<ImportJob>().WithMany().HasForeignKey(x => x.SourceImportJobId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class VoteHistoryEventConfiguration : IEntityTypeConfiguration<VoteHistoryEvent>
{
    public void Configure(EntityTypeBuilder<VoteHistoryEvent> builder)
    {
        // Append-only: UPDATE/DELETE/TRUNCATE are rejected by triggers (see AuditGuardsSql).
        builder.ToTable("vote_history_events");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).UseIdentityAlwaysColumn();
        builder.Property(e => e.Actor).HasMaxLength(64);
        builder.Property(e => e.OldValue).HasMaxLength(512);
        builder.Property(e => e.NewValue).HasMaxLength(512);
        builder.Property(e => e.Comment).HasMaxLength(2000);
        builder.HasIndex(e => new { e.VoteId, e.OccurredAt });

        builder.HasOne<Vote>().WithMany().HasForeignKey(e => e.VoteId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Domain.Conflicts.Conflict>().WithMany().HasForeignKey(e => e.ConflictId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ImportJob>().WithMany().HasForeignKey(e => e.OldSourceImportJobId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ImportJob>().WithMany().HasForeignKey(e => e.NewSourceImportJobId).OnDelete(DeleteBehavior.Restrict);
    }
}
