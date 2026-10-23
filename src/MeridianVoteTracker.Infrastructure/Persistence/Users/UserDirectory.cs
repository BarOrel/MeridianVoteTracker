using MeridianVoteTracker.Application.Users.Ports;
using Microsoft.EntityFrameworkCore;

namespace MeridianVoteTracker.Infrastructure.Persistence.Users;

internal sealed class UserDirectory(AppDbContext db) : IUserDirectory
{
    public Task<UserDto?> FindAsync(string userId, CancellationToken cancellationToken) =>
        db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new UserDto(u.Id, u.DisplayName, u.Role))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<UserDto>> ListAsync(CancellationToken cancellationToken) =>
        await db.Users.AsNoTracking()
            .OrderBy(u => u.Role).ThenBy(u => u.DisplayName)
            .Select(u => new UserDto(u.Id, u.DisplayName, u.Role))
            .ToListAsync(cancellationToken);
}
