using MeridianVoteTracker.Api.Auth;
using MeridianVoteTracker.Application.Conflicts;
using MeridianVoteTracker.Domain.Conflicts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MeridianVoteTracker.Api.Controllers;

public sealed record ResolveConflictRequest(ConflictDecision Decision, string? Comment);

[ApiController]
[Route("api/conflicts")]
[Produces("application/json")]
public sealed class ConflictsController : ControllerBase
{
    /// <summary>Conflicts awaiting a decision (default) or with another status; optionally for one vote (CON-01, CON-06).</summary>
    [HttpGet]
    public Task<IReadOnlyList<ConflictSummaryDto>> List(
        [FromServices] ListConflictsUseCase listConflicts, CancellationToken cancellationToken,
        [FromQuery] ConflictStatus? status = ConflictStatus.Pending, [FromQuery] int? voteId = null) =>
        listConflicts.ExecuteAsync(status, voteId, cancellationToken);

    /// <summary>Approved vs. proposed value with the source of each (CON-02).</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<ConflictDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<ConflictDto> Get(Guid id, [FromServices] GetConflictUseCase getConflict, CancellationToken cancellationToken) =>
        getConflict.ExecuteAsync(id, cancellationToken);

    /// <summary>
    /// Accept or reject (CON-04). 409 if another analyst already decided or the approved value changed meanwhile;
    /// the problem body's <c>current</c> holds the conflict's current state (CON-05).
    /// </summary>
    [HttpPost("{id:guid}/resolution")]
    [Authorize(Policy = AuthorizationPolicies.Analyst)]
    [ProducesResponseType<ConflictDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<ConflictDto> Resolve(Guid id, ResolveConflictRequest request, [FromServices] ResolveConflictUseCase resolve, CancellationToken cancellationToken) =>
        resolve.ExecuteAsync(id, new ResolveConflictCommand(request.Decision, request.Comment), cancellationToken);
}
