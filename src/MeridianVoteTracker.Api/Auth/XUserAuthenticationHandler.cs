using System.Security.Claims;
using System.Text.Encodings.Web;
using MeridianVoteTracker.Application.Users.GetUser;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace MeridianVoteTracker.Api.Auth;

/// <summary>
/// Simplified identity (allowed by the assignment, §3): the UI's user switcher sends the selected
/// pre-defined user id in the X-User-Id header. The user and role are always looked up server-side,
/// so a client cannot claim a role; authorization is then enforced by standard ASP.NET Core policies.
/// Not production authentication: anyone can send any known user id (documented limitation).
/// </summary>
public sealed class XUserAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    GetUserUseCase getUser)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "XUser";
    public const string HeaderName = "X-User-Id";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var userId = Request.Headers[HeaderName].ToString();
        if (string.IsNullOrWhiteSpace(userId))
        {
            return AuthenticateResult.NoResult();
        }

        var user = await getUser.ExecuteAsync(userId, Context.RequestAborted);
        if (user is null)
        {
            return AuthenticateResult.Fail($"Unknown user '{userId}'.");
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, user.Id),
                new Claim(ClaimTypes.Name, user.DisplayName),
                new Claim(ClaimTypes.Role, user.Role.ToString()),
            ],
            SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}
