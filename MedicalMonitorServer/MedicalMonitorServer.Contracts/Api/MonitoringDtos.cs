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

    /// <summary>Invasive arterial pressure, in mmHg.</summary>
    ArterialPressure,

    /// <summary>Central venous pressure, in mmHg.</summary>
    CentralVenousPressure,
}

/// <param name="PatientId">
/// For a patient a station admitted: an id the server made up at admission (a UUID). For one
/// no station has admitted: the id their monitor reports, or "ANON-..." when it reports none.
/// </param>
/// <param name="PatientName">Empty for an admitted patient known by <paramref name="DrAlexId"/> alone.</param>
/// <param name="PatientType">"Adu" (adult), "Ped" (pediatric) or "Neo" (neonatal).</param>
/// <param name="DrAlexId">
/// What the hospital's DrAlex system calls the patient, as typed in at a station. Not checked
/// against anything and not unique: the same person admitted twice has it twice. Null when
/// none was given, and always for a patient no station has admitted.
/// </param>
/// <param name="AdmittedAt">
/// When a station admitted the patient - this time, for somebody who was here before - which
/// is since when their values are recorded (see /api/v1/history). Null for a patient no
/// station has admitted: shown, but not recorded.
/// </param>
public record PatientInfoDto(
    string PatientId,
    string PatientName,
    int BedNumber,
    string? Diagnosis,
    string PatientType = "Adu",
    string? DrAlexId = null,
    DateTimeOffset? AdmittedAt = null);

/// <summary>
/// A patient whose values were recorded, on a monitor now or discharged since: what
/// GET /api/v1/history/patients lists. One patient however many times they were admitted:
/// somebody who comes back is admitted again under the same <paramref name="PatientId"/>.
/// </summary>
/// <param name="PatientId">
/// The id the server made up at the patient's first admission. Sent as existingPatientId to
/// POST /api/v1/patients/{id}/admit, it admits them again into the history they have.
/// </param>
/// <param name="MonitorIp">Monitor the patient is on; null once discharged or while they have none.</param>
/// <param name="AdmittedAt">When a station admitted the patient last: the stay that goes on, or the one that ended last.</param>
/// <param name="DischargedAt">When a station discharged the patient and the recording ended; null while it goes on.</param>
/// <param name="LastRecordedAt">Time of the newest recorded second; null when none was recorded.</param>
/// <param name="Admissions">
/// Every stay of the patient, the first one first; the last is the one <paramref name="AdmittedAt"/>
/// and <paramref name="DischargedAt"/> are of. Nothing was recorded between two of them.
/// </param>
public record RecordedPatientDto(
    string PatientId,
    string? DrAlexId,
    string PatientName,
    int BedNumber,
    string PatientType,
    string? Diagnosis,
    string? MonitorIp,
    DateTimeOffset AdmittedAt,
    DateTimeOffset? DischargedAt,
    DateTimeOffset? LastRecordedAt,
    IReadOnlyList<AdmissionDto> Admissions);

/// <summary>One stay of a recorded patient: from a station admitting them to a station discharging them.</summary>
/// <param name="DischargedAt">Null while the stay goes on.</param>
/// <param name="BedNumber">The bed they were on, as last entered during the stay.</param>
/// <param name="MonitorIp">The monitor they were on, as last known during the stay; null when it was not kept.</param>
public record AdmissionDto(DateTimeOffset AdmittedAt, DateTimeOffset? DischargedAt, int BedNumber, string? MonitorIp);

/// <summary>
/// One recorded second of a patient: what the station showed for them then. A value that was
/// not measured is null. While the monitor reports there is a record for every second - the
/// odd second a once-a-second monitor skips carries what it said last - and a monitor that
/// says nothing for more than three seconds leaves a gap.
/// </summary>
/// <param name="Time">The second, in UTC.</param>
/// <param name="Nibp">
/// A cuff reading, in the second it first appeared and in no other: a reading stands on the
/// monitor until the next one, and repeating it every second would pass it off as measured
/// continuously. Its <see cref="BloodPressureDto.MeasuredAt"/> is the monitor's own time of it.
/// </param>
/// <param name="Parameters">Every other value by <see cref="ParameterDto.Code"/> ("art.sys", "ecg.st.ii", ...); null when there was none.</param>
public record VitalsRecordDto(
    DateTimeOffset Time,
    int? HeartRate,
    int? SpO2,
    int? PulseRate,
    int? RespiratoryRate,
    double? Temperature,
    BloodPressureDto? Nibp,
    IReadOnlyDictionary<string, double>? Parameters);

/// <summary>A bedside monitor and the patient it is currently feeding.</summary>
/// <param name="MonitorIp">IP address of the monitor; identifies it in the transfer endpoint.</param>
/// <param name="BedNumber">Bed the monitor reports; 0 when it has not reported one.</param>
/// <param name="PatientId">Patient whose data this monitor provides; null when nobody is bound.</param>
/// <param name="ReportedPatientId">Patient id the monitor itself sends; empty when it has none, null when not yet known.</param>
/// <param name="Model">
/// Make and model of the monitor, to show which device a bed is on: "Nihon Kohden BSM-3562",
/// "Mindray uMEC". Filled in by the endpoint that lists the monitors; null where it is not
/// known.
/// </param>
/// <param name="CanStartNibp">
/// True when a station may have this monitor start and stop a cuff measurement (POST
/// /api/v1/patients/{patientId}/nibp/start and .../stop): a monitor whose driver can send the
/// command, on a server where that was turned on. Filled in by the endpoint that lists the
/// monitors; false everywhere else, and from a server that does not know the field.
/// </param>
public record MonitorBindingDto(
    string MonitorIp,
    int BedNumber,
    string? PatientId,
    string? ReportedPatientId,
    DateTimeOffset LastSeen,
    string? Model = null,
    bool CanStartNibp = false);

/// <summary>The answer to an accepted request to start or stop a cuff measurement.</summary>
/// <param name="MonitorIp">Monitor the command went to.</param>
/// <param name="Action">"start" or "stop".</param>
public record NibpRequestDto(string PatientId, string MonitorIp, string Action);

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
/// <param name="MeasuredAt">
/// When the cuff reading was taken, as the monitor reports it; null when the monitor does not
/// say. A cuff reading stands until the next one, so its age is part of the reading.
/// </param>
/// <param name="Measuring">
/// True while the monitor reports a cuff measurement running. The values that come with it are
/// the reading before, if the monitor still shows one; the new ones follow when it ends. False
/// from a monitor that does not report it. A station that does not know the field loses nothing.
/// </param>
public record BloodPressureDto(
    int? Systolic,
    int? Diastolic,
    int? MeanArterialPressure,
    DateTimeOffset? MeasuredAt = null,
    bool Measuring = false);

/// <summary>
/// A measured value of the patient's monitor beyond the six every station has a place for:
/// an arterial line, a central venous pressure, a second temperature, ST, PVCs, a perfusion
/// index. Which ones a patient has depends on the monitor and on what is connected to it.
/// </summary>
/// <param name="Code">
/// Stable key a station places the value by: "art.sys", "art.dia", "art.mean", "cvp.mean",
/// "temp.2", "ecg.pvc", "ecg.st.ii", "spo2.pi", ... A measurement the server has no key for
/// comes as "p.{monitor's parameter id}" in the group "other", under the monitor's own label.
/// </param>
/// <param name="Label">Short name to show, e.g. "ART Sys", "T2", "ST-II".</param>
/// <param name="Unit">Unit of <paramref name="Value"/>; empty when it has none.</param>
/// <param name="Group">What it belongs with on a station: "ecg", "spo2", "art", "cvp", "resp", "temp" or "other".</param>
public record ParameterDto(string Code, string Label, double Value, string Unit, string Group);

/// <summary>
/// Latest known vitals. A null value means "no data". On ingestion 0 is still accepted as
/// "not measured" and is stored as null, so the server never sends 0 for a missing reading.
/// </summary>
/// <param name="Parameters">
/// Every other value the monitor measures right now, see <see cref="ParameterDto"/>; null when
/// there is none. A station that does not know the field loses nothing it had before.
/// </param>
public record VitalsDto(
    int? HeartRate,
    int? SpO2,
    BloodPressureDto? Nibp,
    int? RespiratoryRate,
    double? Temperature,
    int? PulseRate,
    IReadOnlyList<ParameterDto>? Parameters = null);

/// <summary>
/// A run of consecutive samples of one waveform, scaled for drawing: an ECG is in
/// millivolts around its zero line, a pressure (arterial, central venous) is in mmHg, any
/// other waveform is centred on 0 and spans about -0.5 .. 0.5. A station plays the samples
/// out at <paramref name="SampleRate"/>.
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
/// <param name="VitalSign">
/// Vital sign the alert is about; null for a device alarm about something else (see
/// <paramref name="Parameter"/>) or about no one value ("lead off"), and for a monitor that
/// does not say which value its alarm is on.
/// </param>
/// <param name="Value">Measured value that breached the threshold; null when not applicable.</param>
/// <param name="Source">Server threshold or monitor-reported alarm.</param>
/// <param name="Code">Device alarm id as sent by the monitor (Mindray: 10002 = HR too low); null for threshold alerts.</param>
/// <param name="Level">Device alarm priority as sent by the monitor; null for threshold alerts.</param>
/// <param name="Category">Physiological or technical; null when unknown.</param>
/// <param name="Parameter">
/// The measured value a device alarm is about when it is not one of the six of
/// <paramref name="VitalSign"/>: the <see cref="ParameterDto.Code"/> of that value ("art.sys",
/// "cvp.mean", "temp.2", "ecg.st.ii", ...), "nibp.mean" for the mean cuff pressure or "pr" for
/// the pulse rate. Null when <paramref name="VitalSign"/> says it, or the monitor does not.
/// A station marks the value in alarm by it, as it does by <paramref name="VitalSign"/>.
/// </param>
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
    AlertCategory? Category = null,
    string? Parameter = null)
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

    /// <summary>
    /// <see cref="Code"/> of the alert the server raises when the monitor of a patient a station
    /// admitted starts naming somebody else, or says its patient was discharged: the recording
    /// goes on under the admitted patient, and somebody has to say whether that is still right.
    /// </summary>
    public const int MonitorPatientChangedCode = -2;

    /// <summary>Key of that alert; outside the device namespace for the same reason.</summary>
    public static string BuildMonitorPatientChangedId(string patientId) => $"{patientId}:monitor-patient-changed";

    /// <summary>Severity the UI uses for a device priority: high is critical, everything else a warning.</summary>
    public static AlertSeverity SeverityOf(DeviceAlarmLevel? level) =>
        level == DeviceAlarmLevel.High ? AlertSeverity.Critical : AlertSeverity.Warning;
}

/// <param name="Status">"Healthy" (fresh data), "Degraded" (data stale or none yet) or "Unhealthy" (patients on the roster but no data and no monitor connected).</param>
/// <param name="ConnectedMonitors">Bedside monitors (uMec devices) currently connected.</param>
/// <param name="LastDataAt">When the last vitals reading arrived; null if none since startup.</param>
/// <param name="DataAgeSeconds">Seconds since <paramref name="LastDataAt"/>; null if none since startup.</param>
/// <param name="History">
/// The recording of admitted patients' values: "Recording", "Failing" (the database cannot be
/// opened or written - nothing is being recorded, whatever a bed says) or "Off" (not set up on
/// this server). Null from a server that records nothing at all; a station then offers neither
/// admission nor discharge.
/// </param>
public record HealthDto(
    string Status,
    DateTimeOffset Timestamp,
    int ActivePatients,
    int ConnectedStations,
    int ConnectedMonitors = 0,
    DateTimeOffset? LastDataAt = null,
    double? DataAgeSeconds = null,
    string? History = null);

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
