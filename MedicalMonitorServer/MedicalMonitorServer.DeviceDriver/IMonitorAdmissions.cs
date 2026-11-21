namespace MedicalMonitorServer.DeviceDriver;

public enum MonitorAdmissionStatus
{
    /// <summary>The monitor acknowledged the message; it now carries the patient id.</summary>
    Accepted,

    /// <summary>The monitor at this address cannot be told who its patient is (a uMec, or unknown).</summary>
    NotSupported,

    /// <summary>The id cannot be stored on the monitor as it is, so nothing was sent.</summary>
    InvalidPatientId,

    /// <summary>The monitor has not reported its bed name yet, and it refuses a message without it.</summary>
    BedUnknown,

    /// <summary>The monitor answered with an error acknowledgement.</summary>
    Rejected,

    /// <summary>No answer: the monitor is off, unplugged or has the function switched off.</summary>
    Unreachable,
}

public sealed record MonitorAdmissionResult(MonitorAdmissionStatus Status, string Detail = "")
{
    public bool Accepted => Status == MonitorAdmissionStatus.Accepted;
}

/// <summary>
/// Writes the patient to the bedside monitor itself, for monitors that take it over the
/// network, so the monitor reports the same id the roster uses.
/// </summary>
public interface IMonitorAdmissions
{
    /// <summary>
    /// Makes <paramref name="patientId"/> the patient of the monitor. A monitor that already has
    /// somebody else admitted starts a new admission; one whose patient merely has no id yet
    /// keeps its data and gets the id.
    /// </summary>
    Task<MonitorAdmissionResult> AssignPatientAsync(
        string monitorIp, string patientId, string patientName, CancellationToken cancellationToken = default);

    /// <summary>Rewrites the name of the patient already admitted on the monitor.</summary>
    Task<MonitorAdmissionResult> UpdatePatientAsync(
        string monitorIp, string patientId, string patientName, CancellationToken cancellationToken = default);
}
