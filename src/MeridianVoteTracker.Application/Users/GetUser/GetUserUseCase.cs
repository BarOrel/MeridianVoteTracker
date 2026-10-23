using MeridianVoteTracker.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace MeridianVoteTracker.Application.Users.GetUser;

/// <summary>Resolves a pre-defined user by id; used by authentication to look up the role server-side.</summary>
public sealed class GetUserUseCase(IApplicationDbContext db)
{
    public Task<UserDto?> ExecuteAsync(string userId, CancellationToken cancellationToken) =>
        db.Users
            .Where(u => u.Id == userId)
            .Select(u => new UserDto(u.Id, u.DisplayName, u.Role))
            .SingleOrDefaultAsync(cancellationToken);
}
