namespace LeadsFinder.Api;

/// <summary>Resumes persisted fetching/enriching jobs after a process restart.</summary>
public sealed class DemoWorker(DemoStore store, ILogger<DemoWorker> logger, IConfiguration configuration) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Clamp(configuration.GetValue("Demo:TickMilliseconds", 750), 10, 5000)));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try { store.Work(); }
                catch (Exception error) { logger.LogError(error, "Unable to persist job progress; processing will retry."); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
