namespace MedicalMonitorServer.DeviceDriver.NihonKohden.LsNet;

/// <summary>
/// When a monitor's cuff reading was taken, for a monitor that does not say: LS-NET repeats
/// the last reading every second and carries no time for it. The reading is timed by when it
/// was seen to arrive - a measurement ending, or the numbers changing - which is right to
/// about the second the monitor takes to report it.
/// <para>
/// A reading that was already there when the stream started was taken nobody knows when, and
/// gets no time: an invented one would make an old reading look fresh.
/// </para>
/// </summary>
internal sealed class CuffClock
{
    private (int? Systolic, int? Diastolic, int? Mean) _reading;
    private bool _measuring;
    private bool _seen;

    /// <summary>When the reading now on the monitor was taken; null when that is not known.</summary>
    public DateTime? TakenAt { get; private set; }

    /// <summary>
    /// The monitor's last report said a measurement is running. Any reading it still lists
    /// while this is true is the one before, about to be replaced.
    /// </summary>
    public bool Measuring => _measuring;

    /// <summary>
    /// Takes the monitor's report of one second.
    /// </summary>
    /// <param name="measuring">The monitor says a measurement is running (cuff inflated).</param>
    /// <returns><see cref="TakenAt"/> after this report.</returns>
    public DateTime? Observe(int? systolic, int? diastolic, int? mean, bool measuring, DateTime now)
    {
        var reading = (systolic, diastolic, mean);
        bool has = systolic is not null || diastolic is not null || mean is not null;

        if (!has)
        {
            TakenAt = null;
        }
        else if (_seen && (reading != _reading || (_measuring && !measuring)))
        {
            // New numbers, or the same numbers out of a measurement that has just ended.
            TakenAt = now;
        }

        _reading = reading;
        _measuring = measuring;
        _seen = true;
        return TakenAt;
    }

    /// <summary>The stream stopped: what the monitor has when it comes back was not seen arriving.</summary>
    public void Reset()
    {
        _reading = default;
        _measuring = false;
        _seen = false;
        TakenAt = null;
    }
}
