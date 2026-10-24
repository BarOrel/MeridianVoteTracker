using MeridianVoteTracker.Api.Auth;
using MeridianVoteTracker.Application.Votes;
using MeridianVoteTracker.Domain.Votes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MeridianVoteTracker.Api.Controllers;

public sealed record SetPositionRequest(Position Position, int Weight, int ExpectedRevision);

[ApiController]
[Route("api/votes")]
[Produces("application/json")]
public sealed class VotesController : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<VoteSummaryDto>> List(
        [FromServices] ListVotesUseCase listVotes, CancellationToken cancellationToken,
        [FromQuery] int? session = null, [FromQuery] DateOnly? from = null, [FromQuery] DateOnly? to = null, [FromQuery] bool withoutPosition = false) =>
        listVotes.ExecuteAsync(session, from, to, withoutPosition, cancellationToken);

    [HttpGet("{id:int}")]
    [ProducesResponseType<VoteDetailsDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<VoteDetailsDto> Get(int id, [FromServices] GetVoteUseCase getVote, CancellationToken cancellationToken) =>
        getVote.ExecuteAsync(id, cancellationToken);

    /// <summary>Full timeline of the vote (HIS-04).</summary>
    [HttpGet("{id:int}/timeline")]
    [ProducesResponseType<IReadOnlyList<VoteHistoryEntryDto>>(StatusCodes.Status200OK)]
    public Task<IReadOnlyList<VoteHistoryEntryDto>> Timeline(int id, [FromServices] GetVoteTimelineUseCase getTimeline, CancellationToken cancellationToken) =>
        getTimeline.ExecuteAsync(id, cancellationToken);

    /// <summary>Admin only (POS-01). 400 with field errors (e.g. weight 7), 409 if changed concurrently.</summary>
    [HttpPut("{id:int}/position")]
    [Authorize(Policy = AuthorizationPolicies.Admin)]
    [ProducesResponseType<VoteDetailsDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<VoteDetailsDto> SetPosition(int id, SetPositionRequest request, [FromServices] SetVotePositionUseCase setPosition, CancellationToken cancellationToken) =>
        setPosition.ExecuteAsync(id, new SetPositionCommand(request.Position, request.Weight, request.ExpectedRevision), cancellationToken);
}
