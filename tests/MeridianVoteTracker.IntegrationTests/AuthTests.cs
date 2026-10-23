using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using MeridianVoteTracker.Api.Auth;
using MeridianVoteTracker.Application.Users.Ports;
using MeridianVoteTracker.Domain.Users;
using MeridianVoteTracker.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MeridianVoteTracker.IntegrationTests;

[Collection(ApiCollection.Name)]
public class AuthTests(ApiFactory factory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task User_list_is_available_without_a_user()
    {
        var users = await _client.GetFromJsonAsync<UserDto[]>("/api/users", Json);

        Assert.NotNull(users);
        Assert.Equal(3, users.Length);
        Assert.Equal(UserRole.Admin, users.Single(u => u.Id == "lena").Role);
    }

    [Fact]
    public async Task Missing_user_header_returns_401_problem_details()
    {
        var response = await _client.GetAsync("/api/users/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Unknown_user_is_not_authenticated()
    {
        var response = await SendAs("mallory", "/api/users/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("dana", UserRole.Analyst)]
    [InlineData("lena", UserRole.Admin)]
    public async Task Known_user_is_resolved_with_role_from_the_server(string userId, UserRole expectedRole)
    {
        var response = await SendAs(userId, "/api/users/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var me = await response.Content.ReadFromJsonAsync<UserDto>(Json);
        Assert.Equal(expectedRole, me!.Role);
    }

    [Theory]
    [InlineData(UserRole.Analyst, AuthorizationPolicies.Analyst, true)]
    [InlineData(UserRole.Analyst, AuthorizationPolicies.Admin, false)]
    [InlineData(UserRole.Admin, AuthorizationPolicies.Admin, true)]
    [InlineData(UserRole.Admin, AuthorizationPolicies.Analyst, true)]
    public async Task Role_policies_are_enforced(UserRole role, string policy, bool allowed)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "x"), new Claim(ClaimTypes.Role, role.ToString())],
            XUserAuthenticationHandler.SchemeName));
        var authorization = factory.Services.GetRequiredService<IAuthorizationService>();

        var result = await authorization.AuthorizeAsync(principal, null, policy);

        Assert.Equal(allowed, result.Succeeded);
    }

    private Task<HttpResponseMessage> SendAs(string userId, string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(XUserAuthenticationHandler.HeaderName, userId);
        return _client.SendAsync(request);
    }
}
