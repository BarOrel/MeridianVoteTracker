using MeridianVoteTracker.Application.Snapshots;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeridianVoteTracker.Infrastructure.BackgroundJobs;

/// <summary>Polls for pending snapshots (SNP-02) and calculates them one at a time; same polling settings as imports.</summary>
internal sealed class SnapshotWorker(IServiceScopeFactory scopes, IOptions<ImportWorkerOptions> options, ILogger<SnapshotWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var worked = false;
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                worked = await scope.ServiceProvider.GetRequiredService<CalculateNextSnapshotUseCase>().ExecuteAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Snapshot worker iteration failed");
            }

            if (!worked)
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
}
