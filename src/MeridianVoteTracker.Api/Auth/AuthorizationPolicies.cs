using MeridianVoteTracker.Domain.Users;
using Microsoft.AspNetCore.Authorization;

namespace MeridianVoteTracker.Api.Auth;

/// <summary>
/// Role policies enforced by the API (not by hiding buttons). Admin is a superset of Analyst:
/// Lena can do analyst work (upload, resolve) in addition to positions/weights and publishing.
/// </summary>
public static class AuthorizationPolicies
{
    public const string Analyst = "Analyst";
    public const string Admin = "Admin";

    public static AuthorizationBuilder AddRolePolicies(this AuthorizationBuilder builder) => builder
        .AddPolicy(Analyst, policy => policy.RequireAuthenticatedUser().RequireRole(nameof(UserRole.Analyst), nameof(UserRole.Admin)))
        .AddPolicy(Admin, policy => policy.RequireAuthenticatedUser().RequireRole(nameof(UserRole.Admin)));
}
