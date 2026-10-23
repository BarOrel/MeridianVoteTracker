using MeridianVoteTracker.Api.Auth;
using MeridianVoteTracker.Application.Imports.GetImportReport;
using MeridianVoteTracker.Application.Imports.GetImportStatus;
using MeridianVoteTracker.Application.Imports.StartImport;
using MeridianVoteTracker.Domain.Imports;
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

    /// <summary>Persisted import report (ING-14/15): totals per classification, conflicts, what happened to each vote.</summary>
    [HttpGet("{id:guid}/report")]
    [ProducesResponseType<ImportReportDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<ImportReportDto> GetReport(Guid id, [FromServices] GetImportReportUseCase getReport, CancellationToken cancellationToken) =>
        getReport.ExecuteAsync(id, cancellationToken);

    /// <summary>
    /// Row-level results with row number, reason and original value. Without <paramref name="classification"/>:
    /// the rows that were not stored as-is (duplicates, invalid, contradictions, conflicts).
    /// </summary>
    [HttpGet("{id:guid}/rows")]
    [ProducesResponseType<ImportRowsPageDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<ImportRowsPageDto> GetRows(
        Guid id,
        [FromQuery] ImportClassification? classification,
        [FromQuery] bool normalizedOnly,
        [FromServices] ListImportRowsUseCase listRows,
        CancellationToken cancellationToken,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 100) =>
        listRows.ExecuteAsync(new ImportRowsQuery(id, classification, normalizedOnly, page, pageSize), cancellationToken);
}
