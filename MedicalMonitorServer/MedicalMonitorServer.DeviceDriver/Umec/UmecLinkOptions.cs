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

    /// <summary>Consecutive sessions without a single message after which a connection gives up.</summary>
    public int MaxFailedSessions { get; init; } = 5;
}
