namespace MeridianVoteTracker.Application.Imports;

/// <param name="BatchSize">Rows per staging transaction: bounds memory and is the progress granularity.</param>
/// <param name="LeaseDuration">How long a claimed job belongs to a worker without a heartbeat; a crashed worker's job is resumed after this.</param>
/// <param name="RetryDelay">Delay before a job is retried after a transient failure (multiplied by the attempt number).</param>
/// <param name="VotesPerClassificationBatch">Votes classified per transaction (~150 rows each): bounds memory and lock scope.</param>
public sealed record ImportProcessingSettings(int BatchSize, TimeSpan LeaseDuration, TimeSpan RetryDelay, int VotesPerClassificationBatch)
{
    public static ImportProcessingSettings Default { get; } = new(5000, TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(15), 200);
}

/// <summary>The worker's ownership of one attempt of one job. Every write checks it is still current.</summary>
public sealed record ImportClaim(Guid ImportJobId, string WorkerId, int Attempt, string? CorrelationId);

/// <summary>Another worker took the job over (this worker's lease expired); this worker must stop without writing.</summary>
public sealed class ImportLeaseLostException(Guid importJobId, Exception? inner = null)
    : Exception($"Lease on import {importJobId} was lost; another worker owns it now.", inner);
