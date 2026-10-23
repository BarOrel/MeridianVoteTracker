using MeridianVoteTracker.Application.Users.Ports;

namespace MeridianVoteTracker.Application.Users.ListUsers;

/// <summary>Lists the users the UI offers in its "switch user" menu.</summary>
public sealed class ListUsersUseCase(IUserDirectory users)
{
    public Task<IReadOnlyList<UserDto>> ExecuteAsync(CancellationToken cancellationToken) =>
        users.ListAsync(cancellationToken);
}
