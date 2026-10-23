using MeridianVoteTracker.Domain.Common;

namespace MeridianVoteTracker.Domain.MemberStates;

/// <summary>
/// ISO 3166-1 alpha-3 code: the identity of a member state (names are not identity, ING-11).
/// Strict: exactly three uppercase ASCII letters. Case normalization is an import concern and
/// must be recorded there (ING-13); this type never silently fixes input.
/// </summary>
public readonly record struct CountryCode
{
    public string Value { get; }

    private CountryCode(string value) => Value = value;

    public static bool TryCreate(string? value, out CountryCode code)
    {
        code = default;
        if (value is not { Length: 3 } || !value.All(char.IsAsciiLetterUpper))
        {
            return false;
        }

        code = new CountryCode(value);
        return true;
    }

    public static CountryCode Create(string? value) =>
        TryCreate(value, out var code)
            ? code
            : throw new DomainException("country_code.invalid", $"'{value}' is not an ISO 3166-1 alpha-3 code.");

    public override string ToString() => Value;
}
