using MeridianVoteTracker.Domain.Users;

namespace MeridianVoteTracker.Application.Users.Ports;

/// <summary>
/// Read access to the pre-defined users. Used by the authentication handler (resolve the
/// X-User-Id header) and by the user switcher in the UI.
/// </summary>
public interface IUserDirectory
{
    Task<UserDto?> FindAsync(string userId, CancellationToken cancellationToken);

    Task<IReadOnlyList<UserDto>> ListAsync(CancellationToken cancellationToken);
}

public sealed record UserDto(string Id, string DisplayName, UserRole Role);
