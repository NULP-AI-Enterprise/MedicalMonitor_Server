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

    /// <summary>
    /// A live stream that has not delivered a byte for this long is treated as
    /// broken. The monitor echoes once a second, so this is ten missed echoes.
    /// Since the watchdog no longer probes a port this driver owns, this is the
    /// only thing that notices a monitor that went silent without hanging up.
    /// </summary>
    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long to wait for the TCP handshake. A monitor that was switched off
    /// or unplugged answers nothing at all, and the OS would retry SYN for over
    /// two minutes before giving up, which would keep this link — and the
    /// watchdog's view of it — stuck for minutes per attempt.
    /// </summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Rejects values that configuration can produce but the driver cannot use.
    /// Worth doing because the failure modes are quiet: a TimeSpan binds from
    /// "hh:mm:ss", so <c>"ReadTimeout": 10</c> means ten <em>days</em> and would
    /// silently disable the only detector of a silently-dead monitor, while a
    /// negative value makes every connect throw. Upper bounds are deliberately
    /// generous — they exist to catch a mistyped unit, not to police tuning.
    /// </summary>
    public void Validate()
    {
        Positive(ReconnectDelay, nameof(ReconnectDelay), TimeSpan.FromMinutes(5));
        Positive(EchoInterval, nameof(EchoInterval), TimeSpan.FromMinutes(1));
        Positive(QueryRefreshInterval, nameof(QueryRefreshInterval), TimeSpan.FromMinutes(10));
        Positive(ReadTimeout, nameof(ReadTimeout), TimeSpan.FromMinutes(10));
        Positive(ConnectTimeout, nameof(ConnectTimeout), TimeSpan.FromMinutes(1));

        if (MaxFailedSessions < 1)
        {
            throw new InvalidOperationException(
                $"Umec:{nameof(MaxFailedSessions)} must be at least 1, but was {MaxFailedSessions}.");
        }

        static void Positive(TimeSpan value, string name, TimeSpan max)
        {
            if (value <= TimeSpan.Zero || value > max)
            {
                throw new InvalidOperationException(
                    $"Umec:{name} must be greater than zero and at most {max}, but was {value}. " +
                    "Configuration binds a TimeSpan from \"hh:mm:ss\", so a bare number means days.");
            }
        }
    }
}
