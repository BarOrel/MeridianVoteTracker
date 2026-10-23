namespace MeridianVoteTracker.Domain.MemberStates;

/// <summary>
/// Immutable in-memory view of all member states (~150), loaded once per import job and used
/// by row validation: "does the code exist?" and "was the state a member on the vote date?" (ING-04).
/// </summary>
public sealed class MembershipRegistry
{
    private readonly Dictionary<CountryCode, MemberState> _states;

    public MembershipRegistry(IEnumerable<MemberState> states) =>
        _states = states.ToDictionary(s => s.Code);

    public int Count => _states.Count;

    public bool Exists(CountryCode code) => _states.ContainsKey(code);

    public bool WasMemberOn(CountryCode code, DateOnly date) =>
        _states.TryGetValue(code, out var state) && state.WasMemberOn(date);
}
