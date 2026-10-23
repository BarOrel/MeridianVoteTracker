using MeridianVoteTracker.Domain.Common;

namespace MeridianVoteTracker.Domain.Votes;

/// <summary>The Institute's desired outcome for a vote (set by an Admin).</summary>
public enum Position
{
    For,
    Against,
}

/// <summary>Vote importance from 1 (marginal) to 5 (critical).</summary>
public readonly record struct Weight
{
    public const int Min = 1;
    public const int Max = 5;

    public int Value { get; }

    private Weight(int value) => Value = value;

    public static Weight Create(int value) =>
        value is >= Min and <= Max
            ? new Weight(value)
            : throw new DomainException("weight.out_of_range", $"Weight must be between {Min} and {Max}; got {value}.");

    public override string ToString() => Value.ToString();
}
