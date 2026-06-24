namespace Lod.LlmGateway.Gateway.Workers;

/// <summary>
/// Periodically inspects worker sessions and evicts stale workers that have not
/// sent a heartbeat for several heartbeat intervals.
/// </summary>
public sealed class WorkerHealthMonitor(
    ILogger<WorkerHealthMonitor> logger,
    WorkerRegistry workerRegistry) : BackgroundService
{
    // Heartbeats are sent from the worker every 30 seconds; consider anything
    // older than 3x that interval as stale.
    static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(60);
    static readonly TimeSpan StaleThreshold = TimeSpan.FromSeconds(90);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTimeOffset.UtcNow;

                foreach (var workerId in workerRegistry.ConnectedWorkerIds)
                {
                    if (!workerRegistry.TryGetById(workerId, out var session) || session is null)
                    {
                        continue;
                    }

                    var age = now - session.LastHeartbeat;
                    if (age > StaleThreshold)
                    {
                        session.Socket.Abort();
                        workerRegistry.Remove(workerId);

                        logger.LogWarning(
                            "Removed stale worker {WorkerId} after {AgeSeconds:F0} seconds without heartbeat. Registered workers: {Count}",
                            workerId,
                            age.TotalSeconds,
                            workerRegistry.RegisteredWorkerCount);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error while checking worker health.");
            }

            try
            {
                await Task.Delay(CheckInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
