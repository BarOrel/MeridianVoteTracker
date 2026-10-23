using MeridianVoteTracker.Domain.Common;
using MeridianVoteTracker.Domain.MemberStates;
using MeridianVoteTracker.Domain.Votes;

namespace MeridianVoteTracker.UnitTests.Domain;

public class ValueObjectTests
{
    [Theory]
    [InlineData("FRA")]
    [InlineData("TUR")]
    public void CountryCode_accepts_three_uppercase_letters(string value) =>
        Assert.True(CountryCode.TryCreate(value, out _));

    [Theory]
    [InlineData("deu")]      // case is normalized (and reported) by the import, never by the value object
    [InlineData(" FRA")]
    [InlineData("FR")]
    [InlineData("FRN1")]
    [InlineData("")]
    [InlineData(null)]
    public void CountryCode_rejects_anything_else(string? value) =>
        Assert.False(CountryCode.TryCreate(value, out _));

    [Theory]
    [InlineData("Y", Ballot.Yes)]
    [InlineData("N", Ballot.No)]
    [InlineData("A", Ballot.Abstain)]
    [InlineData("X", Ballot.Absent)]
    public void Ballot_round_trips_assembly_codes(string code, Ballot expected)
    {
        Assert.True(BallotCodes.TryParse(code, out var ballot));
        Assert.Equal(expected, ballot);
        Assert.Equal(code, ballot.ToCode());
    }

    [Theory]
    [InlineData("Yes")]  // q1 row 2842: rev2 shows the real value was "A"
    [InlineData("YES")]
    [InlineData("y")]
    [InlineData("0")]
    [InlineData("?")]
    [InlineData("")]
    [InlineData(null)]
    public void Ballot_rejects_synonyms_and_case_variants(string? code) =>
        Assert.False(BallotCodes.TryParse(code, out _));

    [Theory]
    [InlineData("MA/2024/003")]
    [InlineData("MA/2015/100")]
    public void AssemblyVoteId_accepts_assembly_format(string value) =>
        Assert.True(AssemblyVoteId.TryCreate(value, out _));

    [Theory]
    [InlineData(" MA/2024/003")]
    [InlineData("ma/2024/003")]
    [InlineData("MA-2024-003")]
    [InlineData("")]
    public void AssemblyVoteId_rejects_malformed_values(string value) =>
        Assert.False(AssemblyVoteId.TryCreate(value, out _));

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(7)]
    public void Weight_outside_1_to_5_is_rejected(int value) =>
        Assert.Throws<DomainException>(() => Weight.Create(value));

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void Weight_bounds_are_inclusive(int value) =>
        Assert.Equal(value, Weight.Create(value).Value);
}
