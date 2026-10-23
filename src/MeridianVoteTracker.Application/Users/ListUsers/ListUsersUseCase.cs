using MeridianVoteTracker.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace MeridianVoteTracker.Application.Users.ListUsers;

/// <summary>Lists the users the UI offers in its "switch user" menu.</summary>
public sealed class ListUsersUseCase(IApplicationDbContext db)
{
    public async Task<IReadOnlyList<UserDto>> ExecuteAsync(CancellationToken cancellationToken) =>
        await db.Users
            .OrderBy(u => u.Role).ThenBy(u => u.DisplayName)
            .Select(u => new UserDto(u.Id, u.DisplayName, u.Role))
            .ToListAsync(cancellationToken);
}
