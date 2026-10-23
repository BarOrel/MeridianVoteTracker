using MeridianVoteTracker.Domain.Common;
using MeridianVoteTracker.Domain.MemberStates;

namespace MeridianVoteTracker.UnitTests.Domain;

public class MemberStateTests
{
    private static DateOnly D(string iso) => DateOnly.Parse(iso);

    [Fact]
    public void State_that_left_and_rejoined_is_a_member_only_inside_its_periods()
    {
        // Iceland in member_states.csv: 1995-01-01..2019-12-31 and 2022-01-01..open.
        var iceland = new MemberState(CountryCode.Create("ISL"), "Iceland");
        iceland.AddPeriod(D("1995-01-01"), D("2019-12-31"));
        iceland.AddPeriod(D("2022-01-01"), null);

        Assert.True(iceland.WasMemberOn(D("2019-12-31")));
        Assert.False(iceland.WasMemberOn(D("2020-06-01")));
        Assert.True(iceland.WasMemberOn(D("2022-01-01")));
        Assert.True(iceland.WasMemberOn(D("2030-01-01")));
        Assert.False(iceland.WasMemberOn(D("1994-12-31")));
    }

    [Fact]
    public void Overlapping_periods_are_rejected()
    {
        var state = new MemberState(CountryCode.Create("NZL"), "New Zealand");
        state.AddPeriod(D("1995-01-01"), D("2021-06-30"));

        var ex = Assert.Throws<DomainException>(() => state.AddPeriod(D("2021-06-30"), null));
        Assert.Equal("membership.overlapping_period", ex.Code);
    }

    [Fact]
    public void Adding_the_same_period_again_is_a_no_op()
    {
        var state = new MemberState(CountryCode.Create("FRA"), "France");
        Assert.True(state.AddPeriod(D("1995-01-01"), null));
        Assert.False(state.AddPeriod(D("1995-01-01"), null));
        Assert.Single(state.Periods);
    }

    [Fact]
    public void Period_cannot_end_before_it_starts()
    {
        var state = new MemberState(CountryCode.Create("FRA"), "France");
        Assert.Throws<DomainException>(() => state.AddPeriod(D("2020-01-01"), D("2019-01-01")));
    }

    [Fact]
    public void Registry_distinguishes_unknown_codes_from_non_members()
    {
        var nzl = new MemberState(CountryCode.Create("NZL"), "New Zealand");
        nzl.AddPeriod(D("1995-01-01"), D("2021-06-30"));
        var registry = new MembershipRegistry([nzl]);

        Assert.True(registry.Exists(CountryCode.Create("NZL")));
        Assert.False(registry.WasMemberOn(CountryCode.Create("NZL"), D("2024-02-28")));
        Assert.False(registry.Exists(CountryCode.Create("FRN")));
    }
}
