using MedicalMonitorServer.Contracts.Api;

namespace MedicalMonitorServer.Core.Monitoring;

/// <summary>Ids of roster entries created for a monitor whose patient has no id.</summary>
public static class AnonymousPatient
{
    public const string IdPrefix = "ANON-";
    public const string DefaultName = "Unidentified patient";

    public static string NewId() => IdPrefix + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    public static bool IsAnonymous(string? patientId) =>
        patientId is not null && patientId.StartsWith(IdPrefix, StringComparison.Ordinal);
}

/// <summary>
/// What changed when a monitor's binding moved. The caller applies it to the roster.
/// </summary>
/// <param name="PatientId">Patient now bound to the monitor; null when the monitor was left without one.</param>
/// <param name="CreatedAnonymous"><paramref name="PatientId"/> is a new anonymous entry that still has to be added to the roster.</param>
/// <param name="SupersededPatientId">Anonymous entry that stood in for this monitor's patient and should be removed.</param>
/// <param name="UnboundPatientId">Identified patient that lost this monitor. Stays on the roster.</param>
/// <param name="PreviousMonitorIp">Monitor <paramref name="PatientId"/> was bound to before.</param>
/// <param name="BedNumber">Bed of the monitor; 0 when it has not reported one.</param>
public sealed record BindingChange(
    string MonitorIp,
    string? PatientId,
    bool CreatedAnonymous,
    string? SupersededPatientId,
    string? UnboundPatientId,
    string? PreviousMonitorIp,
    int BedNumber);

/// <param name="PatientId">Patient the monitor's data belongs to; null when it must be dropped.</param>
/// <param name="DropReason">Why the data is dropped; null when <paramref name="PatientId"/> is set.</param>
/// <param name="Change">Set when the binding changed as a result of this report.</param>
public sealed record RouteResult(string? PatientId, string? DropReason, BindingChange? Change);

public enum TransferStatus
{
    Moved,
    AlreadyBound,
    MonitorUnknown,
}

public sealed record TransferResult(TransferStatus Status, BindingChange? Change);

/// <summary>
/// Which patient each bedside monitor is currently feeding. A monitor (keyed by its IP) is a
/// data source; the patient is a separate entity that can be moved between monitors.
/// <para>
/// The binding follows the patient id the monitor reports, but only when that id changes:
/// a new id binds that patient here (moving them off any other monitor), and an id going
/// empty releases the patient it named. While the reported id stays the same, a binding
/// made by hand with <see cref="Transfer"/> holds.
/// </para>
/// </summary>
public interface IMonitorBindings
{
    IReadOnlyList<MonitorBindingDto> GetAll();

    MonitorBindingDto? Get(string monitorIp);

    /// <summary>Monitor currently feeding the patient, if any.</summary>
    string? FindMonitorOf(string patientId);

    /// <summary>
    /// Records a message from a monitor and returns the patient its data belongs to.
    /// </summary>
    /// <param name="reportedPatientId">
    /// Patient id from the message: null when the message carries no patient identification,
    /// empty when it says the monitor has no patient id.
    /// </param>
    /// <param name="bedNumber">Bed from the message; 0 when unknown.</param>
    /// <param name="hasReadings">The message carries measured values, so somebody is attached.</param>
    RouteResult Report(string monitorIp, string? reportedPatientId, int bedNumber, bool hasReadings, DateTimeOffset now);

    /// <summary>Binds a patient to a monitor by hand, releasing the monitor they were on.</summary>
    TransferResult Transfer(string patientId, string monitorIp);

    /// <summary>
    /// Releases a patient that left the roster. The monitor forgets what it reported, so if
    /// it keeps sending data the patient is registered again.
    /// </summary>
    void Forget(string patientId);
}

public sealed class MonitorBindings : IMonitorBindings
{
    private sealed class Monitor
    {
        public required string Ip { get; init; }
        public string? PatientId;
        public string? Reported;
        public int BedNumber;
        public DateTimeOffset LastSeen;

        public MonitorBindingDto ToDto() => new(Ip, BedNumber, PatientId, Reported, LastSeen);
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, Monitor> _monitors = new(StringComparer.Ordinal);

    public IReadOnlyList<MonitorBindingDto> GetAll()
    {
        lock (_lock)
        {
            return _monitors.Values
                .OrderBy(m => m.Ip, StringComparer.Ordinal)
                .Select(m => m.ToDto())
                .ToList();
        }
    }

    public MonitorBindingDto? Get(string monitorIp)
    {
        lock (_lock)
        {
            return _monitors.TryGetValue(monitorIp, out var monitor) ? monitor.ToDto() : null;
        }
    }

    public string? FindMonitorOf(string patientId)
    {
        lock (_lock)
        {
            return MonitorOf(patientId)?.Ip;
        }
    }

    public RouteResult Report(string monitorIp, string? reportedPatientId, int bedNumber, bool hasReadings, DateTimeOffset now)
    {
        lock (_lock)
        {
            if (!_monitors.TryGetValue(monitorIp, out var monitor))
            {
                _monitors[monitorIp] = monitor = new Monitor { Ip = monitorIp };
            }

            monitor.LastSeen = now;
            if (bedNumber > 0) monitor.BedNumber = bedNumber;

            string? superseded = null, unbound = null, previousMonitor = null;
            var created = false;
            var changed = false;

            if (reportedPatientId is not null && reportedPatientId != monitor.Reported)
            {
                var before = monitor.Reported;
                monitor.Reported = reportedPatientId;

                if (reportedPatientId.Length > 0)
                {
                    if (monitor.PatientId != reportedPatientId)
                    {
                        Detach(monitor, ref superseded, ref unbound);
                        previousMonitor = Release(reportedPatientId);
                        monitor.PatientId = reportedPatientId;
                        changed = true;
                    }
                }
                else if (before is not null && monitor.PatientId == before)
                {
                    // The monitor dropped the id it had: that patient was discharged on the device.
                    unbound = before;
                    monitor.PatientId = null;
                    changed = true;
                }
            }

            // Values with nobody bound and no id to go by: somebody unidentified is attached.
            if (monitor.PatientId is null && string.IsNullOrEmpty(monitor.Reported) && hasReadings)
            {
                monitor.PatientId = AnonymousPatient.NewId();
                created = true;
                changed = true;
            }

            var change = changed
                ? new BindingChange(monitor.Ip, monitor.PatientId, created, superseded, unbound, previousMonitor, monitor.BedNumber)
                : null;

            return monitor.PatientId is null
                ? new RouteResult(null, DropReason(monitor), change)
                : new RouteResult(monitor.PatientId, null, change);
        }
    }

    public TransferResult Transfer(string patientId, string monitorIp)
    {
        lock (_lock)
        {
            if (!_monitors.TryGetValue(monitorIp, out var target))
            {
                return new TransferResult(TransferStatus.MonitorUnknown, null);
            }

            if (target.PatientId == patientId)
            {
                return new TransferResult(TransferStatus.AlreadyBound, null);
            }

            string? superseded = null, unbound = null;
            Detach(target, ref superseded, ref unbound);
            var previousMonitor = Release(patientId);
            target.PatientId = patientId;

            return new TransferResult(
                TransferStatus.Moved,
                new BindingChange(target.Ip, patientId, false, superseded, unbound, previousMonitor, target.BedNumber));
        }
    }

    public void Forget(string patientId)
    {
        lock (_lock)
        {
            var monitor = MonitorOf(patientId);
            if (monitor is null) return;

            monitor.PatientId = null;
            monitor.Reported = null;
        }
    }

    private Monitor? MonitorOf(string patientId) =>
        _monitors.Values.FirstOrDefault(m => m.PatientId == patientId);

    /// <summary>Takes the current patient off a monitor that is about to get another one.</summary>
    private static void Detach(Monitor monitor, ref string? superseded, ref string? unbound)
    {
        if (monitor.PatientId is null) return;

        if (AnonymousPatient.IsAnonymous(monitor.PatientId))
            superseded = monitor.PatientId;
        else
            unbound = monitor.PatientId;

        monitor.PatientId = null;
    }

    /// <summary>Unbinds a patient from whichever monitor has them; returns that monitor's IP.</summary>
    private string? Release(string patientId)
    {
        var monitor = MonitorOf(patientId);
        if (monitor is null) return null;

        monitor.PatientId = null;
        return monitor.Ip;
    }

    private string DropReason(Monitor monitor)
    {
        if (string.IsNullOrEmpty(monitor.Reported)) return "no patient on the monitor";

        var owner = MonitorOf(monitor.Reported);
        return owner is null
            ? $"monitor still reports patient {monitor.Reported}, who was moved off it"
            : $"monitor still reports patient {monitor.Reported}, who is now on monitor {owner.Ip}";
    }
}
