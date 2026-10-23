using MeridianVoteTracker.Domain.Users;

namespace MeridianVoteTracker.Application.Users;

public sealed record UserDto(string Id, string DisplayName, UserRole Role);
