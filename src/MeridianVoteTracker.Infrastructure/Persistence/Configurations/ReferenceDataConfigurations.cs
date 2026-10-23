using MeridianVoteTracker.Domain.MemberStates;
using MeridianVoteTracker.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeridianVoteTracker.Infrastructure.Persistence.Configurations;

internal sealed class MemberStateConfiguration : IEntityTypeConfiguration<MemberState>
{
    public void Configure(EntityTypeBuilder<MemberState> builder)
    {
        builder.ToTable("member_states");
        builder.HasKey(s => s.Code);
        builder.Property(s => s.Name).HasMaxLength(128);

        builder.HasMany(s => s.Periods).WithOne().HasForeignKey(p => p.StateCode).OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(s => s.Periods).HasField("_periods").UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class MembershipPeriodConfiguration : IEntityTypeConfiguration<MembershipPeriod>
{
    public void Configure(EntityTypeBuilder<MembershipPeriod> builder)
    {
        // Non-overlapping periods per state are enforced by an exclusion constraint added in the migration.
        builder.ToTable("membership_periods", t =>
            t.HasCheckConstraint("ck_membership_periods_range", "member_to IS NULL OR member_to >= member_from"));
        builder.HasKey(p => p.Id);
        builder.Property(p => p.From).HasColumnName("member_from");
        builder.Property(p => p.To).HasColumnName("member_to");
        builder.HasIndex(p => new { p.StateCode, p.From }).IsUnique();
    }
}

internal sealed class AppUserConfiguration : IEntityTypeConfiguration<AppUser>
{
    public void Configure(EntityTypeBuilder<AppUser> builder)
    {
        builder.ToTable("users", t => t.HasCheckConstraint("ck_users_role", "role IN ('Analyst', 'Admin')"));
        builder.HasKey(u => u.Id);
        builder.Property(u => u.Id).HasMaxLength(32);
        builder.Property(u => u.DisplayName).HasMaxLength(64);
    }
}
