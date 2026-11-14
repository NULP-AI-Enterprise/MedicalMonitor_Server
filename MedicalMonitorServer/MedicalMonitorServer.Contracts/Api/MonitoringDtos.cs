namespace MedicalMonitorServer.Contracts.Api;

// Wire contract shared with the nurse-station UI (MedicalMonitor_Desktop, docs/API_CONTRACT.md).
// Serialised as camelCase JSON with enums as strings and ISO 8601 timestamps.

public enum AlertSeverity
{
    Warning,
    Critical,
}

public enum VitalSignType
{
    HeartRate,
    SpO2,
    Systolic,
    Diastolic,
    RespiratoryRate,
    Temperature,
}

public enum WaveformType
{
    ECG_II,
    ECG_I,
    Pleth,
    Respiration,
}

/// <param name="PatientType">"Adu" (adult), "Ped" (pediatric) or "Neo" (neonatal).</param>
public record PatientInfoDto(
    string PatientId,
    string PatientName,
    int BedNumber,
    string? Diagnosis,
    string PatientType = "Adu");

/// <summary>A bedside monitor and the patient it is currently feeding.</summary>
/// <param name="MonitorIp">IP address of the monitor; identifies it in the transfer endpoint.</param>
/// <param name="BedNumber">Bed the monitor reports; 0 when it has not reported one.</param>
/// <param name="PatientId">Patient whose data this monitor provides; null when nobody is bound.</param>
/// <param name="ReportedPatientId">Patient id the monitor itself sends; empty when it has none, null when not yet known.</param>
public record MonitorBindingDto(
    string MonitorIp,
    int BedNumber,
    string? PatientId,
    string? ReportedPatientId,
    DateTimeOffset LastSeen);

/// <param name="Patient">The patient after the move (bed follows the monitor).</param>
/// <param name="PreviousMonitorIp">Monitor the patient was on before; null if none.</param>
/// <param name="DisplacedPatientId">Identified patient that was bound to the target monitor and is now without one; null if none.</param>
public record TransferResultDto(
    PatientInfoDto Patient,
    string MonitorIp,
    string? PreviousMonitorIp,
    string? DisplacedPatientId);

public record BloodPressureDto(int Systolic, int Diastolic, int MeanArterialPressure);

public record VitalsDto(
    int HeartRate,
    int SpO2,
    BloodPressureDto Nibp,
    int RespiratoryRate,
    double Temperature,
    int PulseRate);

public record WaveformSampleDto(WaveformType Type, float[] Samples, int SampleRate);

public record MonitorUpdateDto(
    string PatientId,
    string PatientName,
    int BedNumber,
    VitalsDto Vitals,
    WaveformSampleDto[] Waveforms,
    DateTimeOffset Timestamp);

/// <param name="AlertId">Stable key "{patientId}:{vitalSign}".</param>
public record AlertDto(
    string AlertId,
    string PatientId,
    VitalSignType VitalSign,
    AlertSeverity Severity,
    string Message,
    double Value,
    DateTimeOffset Timestamp,
    bool IsAcknowledged = false)
{
    public static string BuildId(string patientId, VitalSignType vitalSign) => $"{patientId}:{vitalSign}";
}

/// <param name="Status">"Healthy" (fresh data), "Degraded" (data stale or none yet) or "Unhealthy" (patients on the roster but no data and no monitor connected).</param>
/// <param name="ConnectedMonitors">Bedside monitors (uMec devices) currently connected.</param>
/// <param name="LastDataAt">When the last vitals reading arrived; null if none since startup.</param>
/// <param name="DataAgeSeconds">Seconds since <paramref name="LastDataAt"/>; null if none since startup.</param>
public record HealthDto(
    string Status,
    DateTimeOffset Timestamp,
    int ActivePatients,
    int ConnectedStations,
    int ConnectedMonitors = 0,
    DateTimeOffset? LastDataAt = null,
    double? DataAgeSeconds = null);

public static class VitalSignLabels
{
    public static string Short(VitalSignType type) => type switch
    {
        VitalSignType.HeartRate => "HR",
        VitalSignType.SpO2 => "SpO2",
        VitalSignType.Systolic => "NIBP Sys",
        VitalSignType.Diastolic => "NIBP Dia",
        VitalSignType.RespiratoryRate => "RESP",
        VitalSignType.Temperature => "TEMP",
        _ => type.ToString(),
    };

    public static string Unit(VitalSignType type) => type switch
    {
        VitalSignType.HeartRate => "bpm",
        VitalSignType.SpO2 => "%",
        VitalSignType.Systolic or VitalSignType.Diastolic => "mmHg",
        VitalSignType.RespiratoryRate => "rpm",
        VitalSignType.Temperature => "°C",
        _ => string.Empty,
    };
}

/// <summary>Canonical SignalR method names of the /hubs/vitals hub.</summary>
public static class HubMethods
{
    public const string Route = "/hubs/vitals";

    public const string ReceiveVitals = "ReceiveVitals";
    public const string PatientAdded = "PatientAdded";
    public const string PatientRemoved = "PatientRemoved";
    public const string AlertTriggered = "AlertTriggered";
    public const string AlertCleared = "AlertCleared";
    public const string AlertAcknowledged = "AlertAcknowledged";
}
