namespace MeridianVoteTracker.Domain.Common;

/// <summary>A business rule was violated. Carries a stable code so the API can map it to a precise HTTP response.</summary>
public class DomainException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
