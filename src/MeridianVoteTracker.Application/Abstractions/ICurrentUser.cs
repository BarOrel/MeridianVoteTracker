using MeridianVoteTracker.Domain.Users;

namespace MeridianVoteTracker.Application.Abstractions;

/// <summary>
/// The authenticated actor of the current operation. Use cases need it for audit fields
/// ("who decided", "who uploaded") without knowing about HTTP headers or claims.
/// </summary>
public interface ICurrentUser
{
    string Id { get; }
    string DisplayName { get; }
    UserRole Role { get; }
}
