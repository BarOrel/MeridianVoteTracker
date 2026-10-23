using MeridianVoteTracker.Application.Imports;
using MeridianVoteTracker.Application.Imports.ProcessImport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeridianVoteTracker.Infrastructure.BackgroundJobs;

public sealed class ImportWorkerOptions
{
    public const string SectionName = "ImportWorker";

    public bool Enabled { get; set; } = true;
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);
    public int BatchSize { get; set; } = ImportProcessingSettings.Default.BatchSize;
    public TimeSpan LeaseDuration { get; set; } = ImportProcessingSettings.Default.LeaseDuration;
    public TimeSpan RetryDelay { get; set; } = ImportProcessingSettings.Default.RetryDelay;
}

/// <summary>
/// Hosted loop over the database-backed queue: claim → process → (on failure) record the failure.
/// Each step gets its own DI scope (fresh DbContext). All decisions live in the use cases; this class
/// only schedules them. Restart recovery needs no special code: a job whose worker died keeps an expiring
/// lease and is claimed again by <see cref="ClaimNextImportUseCase"/>. On shutdown the attempt is
/// abandoned (cancellation) and resumed after its lease expires.
/// </summary>
internal sealed class ImportWorker(IServiceScopeFactory scopes, IOptions<ImportWorkerOptions> options, ILogger<ImportWorker> logger) : BackgroundService
{
    private readonly string _workerId = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid().ToString("N")[..8]}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            logger.LogInformation("Import worker is disabled by configuration");
            return;
        }

        logger.LogInformation("Import worker {WorkerId} started", _workerId);
        while (!stoppingToken.IsCancellationRequested)
        {
            var processedJob = false;
            try
            {
                processedJob = await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // E.g. the database is unreachable while claiming: keep the loop alive and try again later.
                logger.LogError(ex, "Import worker iteration failed");
            }

            if (!processedJob)
            {
                try
                {
                    await Task.Delay(options.Value.PollInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task<bool> RunOnceAsync(CancellationToken stoppingToken)
    {
        ImportClaim? claim;
        await using (var scope = scopes.CreateAsyncScope())
        {
            claim = await scope.ServiceProvider.GetRequiredService<ClaimNextImportUseCase>().ExecuteAsync(_workerId, stoppingToken);
        }

        if (claim is null)
        {
            return false;
        }

        using var _ = logger.BeginScope(new Dictionary<string, object?> { ["CorrelationId"] = claim.CorrelationId, ["ImportJobId"] = claim.ImportJobId });
        logger.LogInformation("Processing import {ImportJobId}, attempt {Attempt}", claim.ImportJobId, claim.Attempt);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ProcessImportUseCase>().ExecuteAsync(claim, stoppingToken);
            logger.LogInformation("Import {ImportJobId} attempt {Attempt} finished", claim.ImportJobId, claim.Attempt);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogWarning("Shutdown during import {ImportJobId}; it resumes after its lease expires", claim.ImportJobId);
            throw;
        }
        catch (ImportLeaseLostException ex)
        {
            logger.LogWarning(ex, "Stopped processing import {ImportJobId}", claim.ImportJobId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Import {ImportJobId} attempt {Attempt} failed", claim.ImportJobId, claim.Attempt);
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<RecordImportFailureUseCase>().ExecuteAsync(claim, ex, CancellationToken.None);
        }

        return true;
    }
}
