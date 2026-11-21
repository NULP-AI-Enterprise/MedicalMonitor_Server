namespace MedicalMonitorServer.Services;

/// <summary>
/// Per-monitor counters behind the periodic Information line of the uMec feed.
/// A monitor sends about ten messages a second, so one line per message would
/// push the alert history out of the log file; this adds them up and yields one
/// line per monitor per interval instead.
/// </summary>
/// <remarks>
/// Not thread-safe: <see cref="UmecFeedService"/> has a single reader. There is
/// no timer either — a window is closed by the first message that arrives after
/// the interval, so a monitor that went quiet reports when it speaks again, and
/// the line carries the real length of its window.
/// </remarks>
public sealed class FeedSummary
{
    /// <summary>What one monitor did over one window.</summary>
    /// <param name="PatientId">The patient it last fed in the window; null when none.</param>
    /// <param name="Vitals">Measured values received for that patient.</param>
    /// <param name="Alarms">Active alarm entries received for that patient.</param>
    /// <param name="Dropped">Messages that carried data but had no patient to go to.</param>
    public readonly record struct Line(
        string MonitorIp,
        string? PatientId,
        int Messages,
        int Vitals,
        int Alarms,
        int Dropped,
        TimeSpan Elapsed);

    private sealed class Window
    {
        public DateTimeOffset Since;
        public string? PatientId;
        public int Messages;
        public int Vitals;
        public int Alarms;
        public int Dropped;
    }

    private readonly Dictionary<string, Window> _windows = new(StringComparer.Ordinal);

    /// <summary>
    /// Counts one message and returns the monitor's summary when its window is over.
    /// </summary>
    /// <param name="patientId">The patient the message was attributed to; null when it was not.</param>
    /// <param name="vitals">Measured values the message carried.</param>
    /// <param name="alarms">Active alarm entries the message carried.</param>
    public Line? Add(string monitorIp, string? patientId, int vitals, int alarms, DateTimeOffset now, TimeSpan interval)
    {
        if (!_windows.TryGetValue(monitorIp, out var window))
        {
            _windows[monitorIp] = window = new Window { Since = now };
        }

        window.Messages++;
        if (patientId is not null)
        {
            window.PatientId = patientId;
            window.Vitals += vitals;
            window.Alarms += alarms;
        }
        else if (vitals > 0 || alarms > 0)
        {
            window.Dropped++;
        }

        if (now - window.Since < interval)
        {
            return null;
        }

        _windows[monitorIp] = new Window { Since = now };
        return new Line(
            monitorIp, window.PatientId, window.Messages, window.Vitals, window.Alarms, window.Dropped,
            now - window.Since);
    }
}
