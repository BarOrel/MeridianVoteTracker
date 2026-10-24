using MeridianVoteTracker.Api.Auth;
using MeridianVoteTracker.Application.Snapshots;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MeridianVoteTracker.Api.Controllers;

public sealed record CreateSnapshotRequest(string Name, DateOnly? From, DateOnly? To, int? Session, IReadOnlyList<string>? AssemblyVoteIds);

public sealed record PublishSnapshotRequest(string Quarter);

[ApiController]
[Route("api/snapshots")]
[Produces("application/json")]
public sealed class SnapshotsController : ControllerBase
{
    /// <summary>Queues a snapshot (SNP-01/02). 400 lists selected votes without a position (POS-04).</summary>
    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.Analyst)]
    [ProducesResponseType<SnapshotDto>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create(CreateSnapshotRequest request, [FromServices] CreateSnapshotUseCase create, CancellationToken cancellationToken)
    {
        var snapshot = await create.ExecuteAsync(new CreateSnapshotCommand(request.Name, request.From, request.To, request.Session, request.AssemblyVoteIds), cancellationToken);
        return AcceptedAtAction(nameof(Get), new { id = snapshot.Snapshot.Id }, snapshot);
    }

    [HttpGet]
    public Task<IReadOnlyList<SnapshotSummaryDto>> List([FromServices] ListSnapshotsUseCase list, CancellationToken cancellationToken) =>
        list.ExecuteAsync(cancellationToken);

    /// <summary>Ranking with the scoring rules used (SNP-03); never changes once completed (SNP-04).</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<SnapshotDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<SnapshotDto> Get(Guid id, [FromServices] GetSnapshotUseCase get, CancellationToken cancellationToken) =>
        get.ExecuteAsync(id, cancellationToken);

    /// <summary>Contribution of every vote to one state's score.</summary>
    [HttpGet("{id:guid}/countries/{code}")]
    [ProducesResponseType<SnapshotCountryDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<SnapshotCountryDto> GetCountry(Guid id, string code, [FromServices] GetSnapshotCountryUseCase get, CancellationToken cancellationToken) =>
        get.ExecuteAsync(id, code, cancellationToken);

    /// <summary>Admin marks the snapshot as the published one for a quarter (SNP-05). 409 if the quarter is already published.</summary>
    [HttpPost("{id:guid}/publish")]
    [Authorize(Policy = AuthorizationPolicies.Admin)]
    [ProducesResponseType<SnapshotDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<SnapshotDto> Publish(Guid id, PublishSnapshotRequest request, [FromServices] PublishSnapshotUseCase publish, CancellationToken cancellationToken) =>
        publish.ExecuteAsync(id, request.Quarter, cancellationToken);
}
