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

/// <summary>Where an alert comes from.</summary>
public enum AlertSource
{
    /// <summary>Raised by the server from its own vital-sign thresholds.</summary>
    Threshold,

    /// <summary>Reported by the bedside monitor itself (or a gateway on its behalf).</summary>
    Device,
}

/// <summary>Kind of a device alarm.</summary>
public enum AlertCategory
{
    /// <summary>Patient condition, e.g. "HR too low".</summary>
    Physiological,

    /// <summary>Equipment condition, e.g. "ECG lead off", "SpO2 sensor off".</summary>
    Technical,
}

/// <summary>Alarm priority exactly as the monitor reports it ("***" / "**" / "*").</summary>
public enum DeviceAlarmLevel
{
    High,
    Medium,
    Low,
}

/// <summary>Every value is null when it is not measured (sensor off, no reading yet).</summary>
public record BloodPressureDto(int? Systolic, int? Diastolic, int? MeanArterialPressure);

/// <summary>
/// Latest known vitals. A null value means "no data". On ingestion 0 is still accepted as
/// "not measured" and is stored as null, so the server never sends 0 for a missing reading.
/// </summary>
public record VitalsDto(
    int? HeartRate,
    int? SpO2,
    BloodPressureDto? Nibp,
    int? RespiratoryRate,
    double? Temperature,
    int? PulseRate);

public record WaveformSampleDto(WaveformType Type, float[] Samples, int SampleRate);

/// <summary>
/// How to draw one waveform channel of the monitor (HL7 OBX 2153). A sample s maps to the
/// physical value (s - Baseline) * Scale [ScaleUnit].
/// </summary>
/// <param name="ChannelId">Monitor channel id: 1101 ECG, 1151 respiration, 1152 Pleth, ...</param>
/// <param name="Type">Known waveform type, or null for a channel the server does not map.</param>
/// <param name="Label">Label shown on the monitor, e.g. "II".</param>
public record WaveformChannelDto(
    int ChannelId,
    WaveformType? Type,
    string Label,
    double? Scale,
    string ScaleUnit,
    int? Baseline,
    int? Levels,
    int? MinSample,
    int? MaxSample);

/// <param name="Waveforms">Waveform samples, when the source provides them (REST gateways). The uMec HL7 feed carries none.</param>
/// <param name="WaveformChannels">Latest known waveform channel descriptors of the patient's monitor; empty when unknown.</param>
public record MonitorUpdateDto(
    string PatientId,
    string PatientName,
    int BedNumber,
    VitalsDto Vitals,
    WaveformSampleDto[]? Waveforms,
    DateTimeOffset Timestamp,
    WaveformChannelDto[]? WaveformChannels = null);

/// <param name="AlertId">
/// Stable key: "{patientId}:{vitalSign}" for alerts tied to a vital sign without a device code,
/// "{patientId}:device:{code}" for device alarms.
/// </param>
/// <param name="VitalSign">Vital sign the alert is about; null for device alarms not tied to one (e.g. "lead off").</param>
/// <param name="Value">Measured value that breached the threshold; null when not applicable.</param>
/// <param name="Source">Server threshold or monitor-reported alarm.</param>
/// <param name="Code">Device alarm id as sent by the monitor (Mindray: 10002 = HR too low); null for threshold alerts.</param>
/// <param name="Level">Device alarm priority as sent by the monitor; null for threshold alerts.</param>
/// <param name="Category">Physiological or technical; null when unknown.</param>
public record AlertDto(
    string AlertId,
    string PatientId,
    VitalSignType? VitalSign,
    AlertSeverity Severity,
    string Message,
    double? Value,
    DateTimeOffset Timestamp,
    bool IsAcknowledged = false,
    AlertSource Source = AlertSource.Threshold,
    int? Code = null,
    DeviceAlarmLevel? Level = null,
    AlertCategory? Category = null)
{
    public static string BuildId(string patientId, VitalSignType vitalSign) => $"{patientId}:{vitalSign}";

    public static string BuildDeviceId(string patientId, int code) => $"{patientId}:device:{code}";

    /// <summary>Severity the UI uses for a device priority: high is critical, everything else a warning.</summary>
    public static AlertSeverity SeverityOf(DeviceAlarmLevel? level) =>
        level == DeviceAlarmLevel.High ? AlertSeverity.Critical : AlertSeverity.Warning;
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
