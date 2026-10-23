using System.Security.Claims;
using MeridianVoteTracker.Application.Abstractions;
using MeridianVoteTracker.Domain.Users;

namespace MeridianVoteTracker.Api.Auth;

/// <summary>Adapts the authenticated HTTP principal to the Application's <see cref="ICurrentUser"/>.</summary>
public sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal Principal =>
        accessor.HttpContext?.User is { Identity.IsAuthenticated: true } user
            ? user
            : throw new InvalidOperationException("No authenticated user in the current request; the endpoint must require authorization.");

    public string Id => Principal.FindFirstValue(ClaimTypes.NameIdentifier)!;

    public string DisplayName => Principal.FindFirstValue(ClaimTypes.Name)!;

    public UserRole Role => Enum.Parse<UserRole>(Principal.FindFirstValue(ClaimTypes.Role)!);
}
