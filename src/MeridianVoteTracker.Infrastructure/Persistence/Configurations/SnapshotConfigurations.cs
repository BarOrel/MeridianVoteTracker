using MeridianVoteTracker.Domain.Snapshots;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeridianVoteTracker.Infrastructure.Persistence.Configurations;

internal sealed class SnapshotConfiguration : IEntityTypeConfiguration<Snapshot>
{
    public void Configure(EntityTypeBuilder<Snapshot> builder)
    {
        // Updates are allowed only while Pending; DELETE never (triggers in the AddSnapshots migration).
        builder.ToTable("snapshots", t => t.HasCheckConstraint("ck_snapshots_status", "status IN ('Pending', 'Completed', 'Failed')"));
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Name).HasMaxLength(Snapshot.MaxNameLength);
        builder.Property(s => s.Selection).HasMaxLength(512);
        builder.Property(s => s.CreatedBy).HasMaxLength(32);
        builder.Property(s => s.Status).HasConversion<string>().HasMaxLength(16);
        builder.Property(s => s.Error).HasMaxLength(2000);
        builder.Ignore(s => s.VoteIds);
        builder.PrimitiveCollection<List<int>>("_voteIds").HasColumnName("vote_ids").IsRequired();
        builder.HasIndex(s => new { s.Status, s.CreatedAt });
    }
}

internal sealed class SnapshotPublicationConfiguration : IEntityTypeConfiguration<SnapshotPublication>
{
    public void Configure(EntityTypeBuilder<SnapshotPublication> builder)
    {
        // One published snapshot per quarter (primary key) and a snapshot is published at most once (unique).
        builder.ToTable("snapshot_publications");
        builder.HasKey(p => p.Quarter);
        builder.Property(p => p.Quarter).HasMaxLength(7);
        builder.Property(p => p.PublishedBy).HasMaxLength(32);
        builder.HasIndex(p => p.SnapshotId).IsUnique();
        builder.HasOne<Snapshot>().WithMany().HasForeignKey(p => p.SnapshotId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class SnapshotVoteConfiguration : IEntityTypeConfiguration<SnapshotVote>
{
    public void Configure(EntityTypeBuilder<SnapshotVote> builder)
    {
        builder.ToTable("snapshot_votes");
        builder.HasKey(v => new { v.SnapshotId, v.VoteId });
        builder.Property(v => v.AssemblyVoteId).HasMaxLength(32);
        builder.Property(v => v.Title).HasMaxLength(512);
        builder.Property(v => v.Position).HasMaxLength(16);
        builder.HasOne<Snapshot>().WithMany().HasForeignKey(v => v.SnapshotId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class SnapshotEntryConfiguration : IEntityTypeConfiguration<SnapshotEntry>
{
    public void Configure(EntityTypeBuilder<SnapshotEntry> builder)
    {
        builder.ToTable("snapshot_entries");
        builder.HasKey(e => new { e.SnapshotId, e.StateCode, e.VoteId });
        builder.Property(e => e.StateCode).HasMaxLength(3).IsFixedLength();
        builder.Property(e => e.Ballot).HasMaxLength(1).IsFixedLength();
        builder.HasOne<SnapshotVote>().WithMany().HasForeignKey(e => new { e.SnapshotId, e.VoteId }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class SnapshotResultConfiguration : IEntityTypeConfiguration<SnapshotResult>
{
    public void Configure(EntityTypeBuilder<SnapshotResult> builder)
    {
        builder.ToTable("snapshot_results");
        builder.HasKey(r => new { r.SnapshotId, r.StateCode });
        builder.Property(r => r.StateCode).HasMaxLength(3).IsFixedLength();
        builder.Property(r => r.Score).HasPrecision(4, 1);
        builder.HasOne<Snapshot>().WithMany().HasForeignKey(r => r.SnapshotId).OnDelete(DeleteBehavior.Restrict);
    }
}
