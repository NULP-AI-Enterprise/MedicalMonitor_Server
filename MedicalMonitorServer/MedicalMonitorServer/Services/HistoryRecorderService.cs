namespace MedicalMonitorServer.Services;

/// <summary>
/// Keeps the history of admitted patients going: puts them back on their monitors when the
/// server starts, then writes each second's rows, once a second.
/// <para>
/// Registered before the feed (Program.cs): hosted services start one after another, so the
/// patients are back on the roster before the first message of a monitor is looked at, and that
/// message finds its patient instead of making up an entry for somebody unknown.
/// </para>
/// </summary>
public sealed class HistoryRecorderService : BackgroundService
{
    private static readonly TimeSpan SizeLogInterval = TimeSpan.FromHours(24);

    private readonly MonitorRouter _router;
    private readonly PatientHistory _history;
    private readonly ILogger<HistoryRecorderService> _logger;

    public HistoryRecorderService(MonitorRouter router, PatientHistory history, ILogger<HistoryRecorderService> logger)
    {
        _router = router;
        _history = history;
        _logger = logger;
    }

    /// <summary>How often what came in is written. A second: that is what a row of the history is.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(1);

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _router.RestoreAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The server must come up without them: the monitors are shown either way, and
            // what is lost is that the patients on them have to be admitted again.
            _logger.LogError(ex, "The admitted patients could not be put back from the history database.");
        }

        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        var sizeLoggedAt = DateTimeOffset.UtcNow;

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var now = DateTimeOffset.UtcNow;

                // Flush catches what the database throws; nothing here can stop the host.
                _history.Flush(now);

                if (now - sizeLoggedAt >= SizeLogInterval)
                {
                    sizeLoggedAt = now;
                    _history.LogSize();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);

        // What came in since the last tick. A server that is killed instead of stopped loses
        // this much and no more.
        _history.Flush(DateTimeOffset.UtcNow);
    }
}
