namespace MedicalMonitorServer.Services;

/// <summary>
/// Periodically asks <see cref="MonitorRouter.SweepSilentAsync"/> which monitors have
/// gone silent. Nothing else notices that: values only move when a message arrives, so a
/// monitor that stops sending would otherwise leave its last numbers on screen as if
/// they were current, with no alert.
/// </summary>
public sealed class MonitorSilenceService : BackgroundService
{
    private readonly MonitorRouter _router;
    private readonly ILogger<MonitorSilenceService> _logger;
    private readonly TimeSpan _silentAfter;

    public MonitorSilenceService(
        MonitorRouter router,
        IConfiguration configuration,
        ILogger<MonitorSilenceService> logger)
    {
        _router = router;
        _logger = logger;

        // The same threshold /health uses to call the feed stale.
        var seconds = configuration.GetValue("Health:StaleAfterSeconds", 30);
        _silentAfter = TimeSpan.FromSeconds(seconds > 0 ? seconds : 30);
    }

    /// <summary>How often the monitors are checked; also the longest a cleared silence takes to show.</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await _router.SweepSilentAsync(DateTimeOffset.UtcNow, _silentAfter, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // An exception escaping a BackgroundService stops the host; one failed
                    // sweep must not take the server down, the next tick tries again.
                    _logger.LogError(ex, "Checking for silent monitors failed.");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
