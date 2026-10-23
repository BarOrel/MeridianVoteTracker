using MeridianVoteTracker.Domain.Common;

namespace MeridianVoteTracker.Domain.MemberStates;

/// <summary>
/// A member state of the Assembly. Invariant: membership periods never overlap
/// (also enforced in PostgreSQL by an exclusion constraint).
/// </summary>
public sealed class MemberState
{
    private readonly List<MembershipPeriod> _periods = [];

    public CountryCode Code { get; private set; }
    public string Name { get; private set; } = null!;
    public IReadOnlyList<MembershipPeriod> Periods => _periods;

    private MemberState() { }

    public MemberState(CountryCode code, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainException("member_state.name_required", $"{code}: name is required.");
        }

        Code = code;
        Name = name.Trim();
    }

    /// <returns>false when an identical period already exists (idempotent seeding).</returns>
    public bool AddPeriod(DateOnly from, DateOnly? to)
    {
        if (_periods.Any(p => p.From == from && p.To == to))
        {
            return false;
        }

        if (_periods.Any(p => p.Overlaps(from, to)))
        {
            throw new DomainException("membership.overlapping_period", $"{Code}: period {from}..{to?.ToString() ?? "open"} overlaps an existing period.");
        }

        _periods.Add(new MembershipPeriod(Code, from, to));
        return true;
    }

    public bool WasMemberOn(DateOnly date) => _periods.Any(p => p.Contains(date));
}
