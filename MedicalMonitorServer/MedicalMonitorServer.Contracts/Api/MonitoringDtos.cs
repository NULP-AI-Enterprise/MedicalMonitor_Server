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

/// <summary>
/// A run of consecutive samples of one waveform, scaled for drawing: an ECG is in
/// millivolts around its zero line, any other waveform is centred on 0 and spans about
/// -0.5 .. 0.5. A station plays the samples out at <paramref name="SampleRate"/>.
/// </summary>
/// <param name="SampleRate">
/// Samples per second, set by the source: a uMec sends ECG and Pleth at 256 Hz and
/// respiration at 128 Hz, a second at a time.
/// </param>
public record WaveformSampleDto(WaveformType Type, float[] Samples, int SampleRate);

/// <summary>
/// One waveform channel as the monitor describes it (HL7 OBX 2153). A raw sample s of the
/// channel is the physical value (s - Baseline) * Scale [ScaleUnit]. Stations do not need
/// this to draw, <see cref="WaveformSampleDto"/> samples are already scaled; it tells which
/// lead a channel carries and what its trace was scaled from.
/// </summary>
/// <param name="ChannelId">Monitor channel id: 1101 ECG, 1151 respiration, 1152 Pleth, ...</param>
/// <param name="Type">Known waveform type, or null for a channel the server does not map.</param>
/// <param name="Label">Label shown on the monitor, e.g. "II".</param>
/// <param name="SampleRate">Samples per second of the channel.</param>
public record WaveformChannelDto(
    int ChannelId,
    WaveformType? Type,
    string Label,
    double? Scale,
    string ScaleUnit,
    int? Baseline,
    int? SampleRate,
    int? MinSample,
    int? MaxSample);

/// <param name="Waveforms">
/// Waveform samples that came with this frame; empty when there are none. A REST gateway
/// may send every trace in one frame, a uMec sends each channel in a frame of its own.
/// </param>
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

    /// <summary>
    /// <see cref="Code"/> of the alert the server raises when a patient's monitor has stopped
    /// sending anything. Negative, so it cannot be one of the monitor's own alarm ids.
    /// </summary>
    public const int MonitorSilentCode = -1;

    /// <summary>
    /// Key of the "monitor is silent" alert. Deliberately outside the "{patientId}:device:{code}"
    /// namespace, which a gateway can write to with any code it likes.
    /// </summary>
    public static string BuildMonitorSilentId(string patientId) => $"{patientId}:monitor-silent";

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
