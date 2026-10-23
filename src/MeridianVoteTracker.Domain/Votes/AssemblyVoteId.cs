using System.Text.RegularExpressions;
using MeridianVoteTracker.Domain.Common;

namespace MeridianVoteTracker.Domain.Votes;

/// <summary>
/// The Assembly's permanent vote identifier, e.g. "MA/2024/003". Strict format (no trimming,
/// no case folding): body prefix, 4-digit year, numeric sequence.
/// </summary>
public readonly partial record struct AssemblyVoteId
{
    public const int MaxLength = 32;

    public string Value { get; }

    private AssemblyVoteId(string value) => Value = value;

    public static bool TryCreate(string? value, out AssemblyVoteId id)
    {
        id = default;
        if (value is null || value.Length > MaxLength || !Format().IsMatch(value))
        {
            return false;
        }

        id = new AssemblyVoteId(value);
        return true;
    }

    public static AssemblyVoteId Create(string? value) =>
        TryCreate(value, out var id)
            ? id
            : throw new DomainException("vote_id.invalid", $"'{value}' is not a valid Assembly vote id.");

    public override string ToString() => Value;

    [GeneratedRegex(@"^[A-Z]{2,5}/\d{4}/\d{1,6}$", RegexOptions.CultureInvariant)]
    private static partial Regex Format();
}
