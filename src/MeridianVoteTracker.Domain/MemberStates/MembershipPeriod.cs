using MeridianVoteTracker.Domain.Common;

namespace MeridianVoteTracker.Domain.MemberStates;

/// <summary>An inclusive membership interval. <see cref="To"/> null means "still a member".</summary>
public sealed class MembershipPeriod
{
    public int Id { get; private set; }
    public CountryCode StateCode { get; private set; }
    public DateOnly From { get; private set; }
    public DateOnly? To { get; private set; }

    private MembershipPeriod() { }

    internal MembershipPeriod(CountryCode stateCode, DateOnly from, DateOnly? to)
    {
        if (to is { } end && end < from)
        {
            throw new DomainException("membership.invalid_period", $"{stateCode}: membership ends ({end}) before it starts ({from}).");
        }

        StateCode = stateCode;
        From = from;
        To = to;
    }

    public bool Contains(DateOnly date) => date >= From && (To is null || date <= To);

    public bool Overlaps(DateOnly from, DateOnly? to) =>
        (To is null || from <= To) && (to is null || From <= to);
}
