using MeridianVoteTracker.Api.Auth;
using MeridianVoteTracker.Application.Imports.GetImportStatus;
using MeridianVoteTracker.Application.Imports.StartImport;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MeridianVoteTracker.Api.Controllers;

[ApiController]
[Route("api/imports")]
[Produces("application/json")]
public sealed class ImportsController : ControllerBase
{
    /// <summary>The historic export is ~100 MB; leave headroom.</summary>
    private const long MaxUploadBytes = 256L * 1024 * 1024;

    /// <summary>
    /// Uploads an Assembly export. Returns 202 immediately with the import's status and a Location header
    /// to poll; validation and staging run in the background (ING-01).
    /// </summary>
    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.Analyst)]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaxUploadBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxUploadBytes)]
    [ProducesResponseType<ImportStatusDto>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status415UnsupportedMediaType)]
    public async Task<IActionResult> Upload(
        IFormFile file,
        [FromServices] StartImportUseCase startImport,
        [FromServices] GetImportStatusUseCase getStatus,
        CancellationToken cancellationToken)
    {
        await using var content = file.OpenReadStream();
        var started = await startImport.ExecuteAsync(new StartImportCommand(file.FileName, content, HttpContext.TraceIdentifier), cancellationToken);
        var status = await getStatus.ExecuteAsync(started.ImportJobId, cancellationToken);
        return AcceptedAtAction(nameof(GetStatus), new { id = started.ImportJobId }, status);
    }

    /// <summary>Current state and progress of an import (ING-02); survives page refreshes.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<ImportStatusDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<ImportStatusDto> GetStatus(Guid id, [FromServices] GetImportStatusUseCase getStatus, CancellationToken cancellationToken) =>
        getStatus.ExecuteAsync(id, cancellationToken);
}
