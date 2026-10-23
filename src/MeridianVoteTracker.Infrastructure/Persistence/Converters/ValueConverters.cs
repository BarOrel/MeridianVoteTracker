using MeridianVoteTracker.Domain.MemberStates;
using MeridianVoteTracker.Domain.Votes;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace MeridianVoteTracker.Infrastructure.Persistence.Converters;

// Value objects are stored as their canonical primitive. Reading goes through Create(...),
// so a corrupted value in the database fails loudly instead of flowing into the domain.

internal sealed class CountryCodeConverter() : ValueConverter<CountryCode, string>(c => c.Value, v => CountryCode.Create(v));

internal sealed class AssemblyVoteIdConverter() : ValueConverter<AssemblyVoteId, string>(id => id.Value, v => AssemblyVoteId.Create(v));

internal sealed class WeightConverter() : ValueConverter<Weight, short>(w => (short)w.Value, v => Weight.Create(v));

/// <summary>Ballots are stored as the Assembly's own codes (Y/N/A/X), readable in SQL and in the audit trail.</summary>
internal sealed class BallotConverter() : ValueConverter<Ballot, string>(b => b.ToCode(), v => ParseBallot(v))
{
    private static Ballot ParseBallot(string code) =>
        BallotCodes.TryParse(code, out var ballot) ? ballot : throw new InvalidOperationException($"Invalid ballot '{code}' in database.");
}
