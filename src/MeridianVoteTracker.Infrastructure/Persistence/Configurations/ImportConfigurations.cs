using MeridianVoteTracker.Domain.Conflicts;
using MeridianVoteTracker.Domain.Imports;
using MeridianVoteTracker.Domain.Votes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeridianVoteTracker.Infrastructure.Persistence.Configurations;

internal sealed class ImportJobConfiguration : IEntityTypeConfiguration<ImportJob>
{
    public void Configure(EntityTypeBuilder<ImportJob> builder)
    {
        // Updates after finalization and any DELETE are rejected by triggers (see AuditGuardsSql).
        builder.ToTable("import_jobs", t =>
        {
            t.HasCheckConstraint("ck_import_jobs_status", "status IN ('Queued', 'Processing', 'Completed', 'CompletedWithIssues', 'Failed')");
            t.HasCheckConstraint("ck_import_jobs_phase", "phase IN ('Pending', 'Staging', 'Classifying', 'Finalized')");
            t.HasCheckConstraint("ck_import_jobs_counters", "staged_rows >= 0 AND classified_rows >= 0 AND attempts >= 0 AND (total_rows IS NULL OR total_rows >= 0)");
            t.HasCheckConstraint("ck_import_jobs_sha256", "sha256 ~ '^[0-9a-f]{64}$'");
        });
        builder.HasKey(j => j.Id);
        builder.Property(j => j.OriginalFileName).HasMaxLength(260);
        builder.Property(j => j.StorageKey).HasMaxLength(260);
        builder.Property(j => j.Sha256).HasMaxLength(64).IsFixedLength();
        builder.Property(j => j.Format).HasMaxLength(16);
        builder.Property(j => j.UploadedBy).HasMaxLength(32);
        builder.Property(j => j.CorrelationId).HasMaxLength(64);
        builder.Property(j => j.LeaseOwner).HasMaxLength(128);
        builder.Property(j => j.Error).HasMaxLength(ImportJob.MaxErrorLength);

        // A worker whose lease was taken over (attempt incremented by another worker) can no longer write:
        // UPDATE import_jobs ... WHERE id = @id AND attempts = @myAttempt.
        builder.Property(j => j.Attempts).IsConcurrencyToken();
        builder.Ignore(j => j.IsFinalized);

        builder.HasIndex(j => new { j.Status, j.UploadedAt });
        builder.HasIndex(j => j.Sha256);
    }
}

internal sealed class ImportRowConfiguration : IEntityTypeConfiguration<ImportRow>
{
    public void Configure(EntityTypeBuilder<ImportRow> builder)
    {
        builder.ToTable("import_rows", t =>
        {
            t.HasCheckConstraint("ck_import_rows_classification",
                "classification IS NULL OR classification IN ('New', 'Unchanged', 'Duplicate', 'Invalid', 'Contradiction', 'Conflict')");
            t.HasCheckConstraint("ck_import_rows_duplicate", "(classification = 'Duplicate') = (duplicate_kind IS NOT NULL AND duplicate_of_row_number IS NOT NULL)");
            t.HasCheckConstraint("ck_import_rows_conflict", "(classification = 'Conflict') = (conflict_id IS NOT NULL)");
            // A valid (normalized) row is never Invalid; an invalid row never has normalized values.
            t.HasCheckConstraint("ck_import_rows_normalized", "(assembly_vote_id IS NULL) = (classification = 'Invalid')");
        });
        builder.HasKey(r => new { r.ImportJobId, r.RowNumber });
        builder.HasOne<ImportJob>().WithMany().HasForeignKey(r => r.ImportJobId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Conflict>().WithMany().HasForeignKey(r => r.ConflictId).OnDelete(DeleteBehavior.Restrict);

        builder.Property(r => r.OriginalText).HasMaxLength(ImportRow.MaxOriginalTextLength);
        builder.Property(r => r.Title).HasMaxLength(ImportRow.MaxTitleLength);
        builder.Property(r => r.CountryName).HasMaxLength(ImportRow.MaxCountryNameLength);
        builder.Property(r => r.ReasonCode).HasMaxLength(64);
        builder.Property(r => r.Message).HasMaxLength(ImportRow.MaxMessageLength);
        // Field-mapped text[]: the domain exposes the notes read-only.
        builder.Ignore(r => r.NormalizationNotes);
        builder.PrimitiveCollection<List<string>>("_normalizationNotes").HasColumnName("normalization_notes").IsRequired();
        builder.Ignore(r => r.Raw);
        builder.Ignore(r => r.Normalized);
        builder.Ignore(r => r.IsNormalized);

        // Classification groups rows by vote (rows of one vote are not contiguous in the file, ING-03);
        // the report filters by classification.
        builder.HasIndex(r => new { r.ImportJobId, r.AssemblyVoteId });
        builder.HasIndex(r => new { r.ImportJobId, r.Classification });
    }
}

internal sealed class ImportVoteOutcomeConfiguration : IEntityTypeConfiguration<ImportVoteOutcome>
{
    public void Configure(EntityTypeBuilder<ImportVoteOutcome> builder)
    {
        // Insert-only: written in the classification batch transaction, then never changed (triggers).
        builder.ToTable("import_vote_outcomes", t =>
        {
            t.HasCheckConstraint("ck_import_vote_outcomes_outcome",
                "outcome IN ('NewVote', 'CountriesAdded', 'ConflictsDetected', 'NoChange', 'BlockedByContradiction')");
            t.HasCheckConstraint("ck_import_vote_outcomes_counts",
                "new_rows >= 0 AND unchanged_rows >= 0 AND duplicate_rows >= 0 AND contradiction_rows >= 0 AND conflict_rows >= 0 AND conflicts_created >= 0");
        });
        builder.HasKey(o => new { o.ImportJobId, o.AssemblyVoteId });
        builder.HasOne<ImportJob>().WithMany().HasForeignKey(o => o.ImportJobId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Vote>().WithMany().HasForeignKey(o => o.VoteId).OnDelete(DeleteBehavior.Restrict);
        builder.Ignore(o => o.Notes);
        builder.PrimitiveCollection<List<string>>("_notes").HasColumnName("notes").IsRequired();
    }
}
