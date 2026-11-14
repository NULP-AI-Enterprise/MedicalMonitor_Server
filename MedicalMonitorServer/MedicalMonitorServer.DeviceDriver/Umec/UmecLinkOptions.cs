namespace MedicalMonitorServer.DeviceDriver.Umec;

/// <summary>
/// Timings of one monitor link. The defaults are what a uMec10 expects on a
/// real network; tests shorten them so a reconnect sequence takes milliseconds.
/// </summary>
public sealed record UmecLinkOptions
{
    /// <summary>Pause before dialling the monitor again after a failed session.</summary>
    public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How often to send the keep-alive echo. The monitor wants to hear from
    /// us at least every 10 seconds; half that leaves room for one lost echo.
    /// </summary>
    public TimeSpan EchoInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How often to repeat the start query on a live session. A uMec10 (SW 01.10.00)
    /// ignores the query it gets right after connecting, while it is still sending
    /// its settings, so periodic parameters (HR, SpO2, RR) only start with a later
    /// one. Repeating it also re-subscribes after the monitor reloads its modules
    /// (e.g. leaving demo mode). A repeated query does not trigger another settings dump.
    /// </summary>
    public TimeSpan QueryRefreshInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Consecutive sessions without a single message after which a connection gives up.</summary>
    public int MaxFailedSessions { get; init; } = 5;
}
