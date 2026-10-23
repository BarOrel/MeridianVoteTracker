using MeridianVoteTracker.Application.Abstractions;
using MeridianVoteTracker.Application.Users;
using MeridianVoteTracker.Application.Users.ListUsers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MeridianVoteTracker.Api.Controllers;

[ApiController]
[Route("api/users")]
[Produces("application/json")]
public sealed class UsersController : ControllerBase
{
    /// <summary>Pre-defined users for the "switch user" menu. Anonymous: it is needed before a user is chosen.</summary>
    [HttpGet]
    [AllowAnonymous]
    [ProducesResponseType<IReadOnlyList<UserDto>>(StatusCodes.Status200OK)]
    public Task<IReadOnlyList<UserDto>> List([FromServices] ListUsersUseCase useCase, CancellationToken cancellationToken) =>
        useCase.ExecuteAsync(cancellationToken);

    /// <summary>The user identified by the X-User-Id header.</summary>
    [HttpGet("me")]
    [ProducesResponseType<UserDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public UserDto Me([FromServices] ICurrentUser currentUser) =>
        new(currentUser.Id, currentUser.DisplayName, currentUser.Role);
}
