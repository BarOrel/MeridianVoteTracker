using MeridianVoteTracker.Domain.MemberStates;
using MeridianVoteTracker.Domain.Users;
using MeridianVoteTracker.Infrastructure.Persistence.Seed;
using MeridianVoteTracker.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace MeridianVoteTracker.IntegrationTests.Persistence;

[Collection(ApiCollection.Name)]
public class SeedTests(ApiFactory factory)
{
    private readonly TestData _data = new(factory);

    [Fact]
    public async Task Startup_seeds_all_member_states_periods_and_users()
    {
        await using var scope = _data.OpenScope();

        Assert.Equal(150, await scope.Db.MemberStates.CountAsync());
        Assert.Equal(151, await scope.Db.Set<MembershipPeriod>().CountAsync());
        var users = await scope.Db.Users.OrderBy(u => u.Id).Select(u => new { u.Id, u.Role }).ToListAsync();
        Assert.Equal(["dana", "lena", "omar"], users.Select(u => u.Id));
        Assert.Equal(UserRole.Admin, users.Single(u => u.Id == "lena").Role);

        var iceland = await scope.Db.MemberStates.Include(s => s.Periods).SingleAsync(s => s.Code == CountryCode.Create("ISL"));
        Assert.Equal(2, iceland.Periods.Count);
        Assert.False(iceland.WasMemberOn(new DateOnly(2021, 1, 1)));
    }

    [Fact]
    public async Task Seeding_again_changes_nothing()
    {
        await using var scope = _data.OpenScope();
        var initializer = scope.Services.GetRequiredService<DatabaseInitializer>();

        var result = await initializer.InitializeAsync(CancellationToken.None);

        Assert.Equal(new SeedResult(0, 0, 0), result);
    }

    [Fact]
    public async Task Database_rejects_overlapping_membership_periods()
    {
        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            _data.ExecuteSqlAsync("INSERT INTO membership_periods (state_code, member_from, member_to) VALUES ('ISL', '2019-06-01', NULL)"));

        Assert.Equal(PostgresErrorCodes.ExclusionViolation, ex.SqlState);
    }
}
