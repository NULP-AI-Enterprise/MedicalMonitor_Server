namespace MedicalMonitorServer.Contracts.Models;

/// <summary>
/// What a uMec10 HL7 message carries. Derived from MSH-10 (Mindray's
/// "message id"); falls back to the message content when the id is unknown.
/// </summary>
public enum UmecMessageKind
{
    Unknown = 0,

    /// <summary>Patient demographics, bed, height/weight (msg 103).</summary>
    PatientInfo,

    /// <summary>Parameter id -> display label announcements (msg 11, 1202).</summary>
    ParameterLabels,

    /// <summary>High/low alarm limits per parameter (msg 51).</summary>
    AlarmLimits,

    /// <summary>Alarm on/off per parameter (msg 60).</summary>
    AlarmSwitches,

    /// <summary>Alarm priority per parameter (msg 58).</summary>
    AlarmLevels,

    /// <summary>Alarm volume / mute / pause state of the monitor (msg 53).</summary>
    AlarmSystemStatus,

    /// <summary>Currently active physiological alarms; empty list = none (msg 54).</summary>
    PhysiologicalAlarms,

    /// <summary>Currently active technical alarms; empty list = none (msg 56).</summary>
    TechnicalAlarms,

    /// <summary>Measured values, e.g. an NIBP reading (msg 503).</summary>
    Vitals,

    /// <summary>Module / waveform / display settings we do not interpret (msg 5, 251, 451, ...).</summary>
    Settings,

    /// <summary>ADT^A01 "online notification" the monitor broadcasts once a second on UDP 4600/4620.</summary>
    MonitorStatus,

    /// <summary>Any other ADT message (admit / discharge / transfer).</summary>
    AdtEvent,
}

/// <summary>
/// One decoded HL7 message from a Mindray uMec10.
/// Contains HL7 header fields shared by both message types and exactly one of
/// <see cref="Oru"/> (ORU^R01 from the TCP stream) or
/// <see cref="Adt"/> (ADT^A01 from the UDP beacon).
/// </summary>
public sealed class UmecMessage
{
    public UmecMessageKind Kind { get; set; }

    /// <summary>MSH-10 as sent.</summary>
    public string ControlId { get; set; } = "";

    /// <summary>MSH-10 as a number: Mindray's ORU message id (103 = patient info, 503 = NIBP, ...); 0 when not numeric.</summary>
    public int MessageId { get; set; }

    /// <summary>MSH-9, e.g. "ORU^R01".</summary>
    public string MessageType { get; set; } = "";

    /// <summary>MSH-12, e.g. "2.3.1".</summary>
    public string Hl7Version { get; set; } = "";

    public DateTime ReceivedAt { get; set; }

    /// <summary>ORU^R01 payload: vitals, alarms, settings, etc. Null for ADT messages.</summary>
    public UmecVitalsMessage? Oru { get; set; }

    /// <summary>ADT payload: monitor identity and patient demographics. Null for ORU messages.</summary>
    public DeviceAdtInfo? Adt { get; set; }

    /// <summary>
    /// The message as received, without MLLP framing, segments separated by CR.
    /// Bytes are carried as Latin-1 chars (one char per byte), so
    /// <c>Encoding.Latin1.GetBytes(Raw)</c> gives back the exact wire bytes.
    /// </summary>
    public string Raw { get; set; } = "";
}

/// <summary>
/// ORU^R01 payload from the TCP stream (port 4601): measured vitals, alarm
/// configuration, active alarms, parameter labels, and the raw OBX segments.
/// </summary>
public sealed class UmecVitalsMessage
{
    public PatientInfo? Patient { get; set; }

    public List<VitalSign> Vitals { get; set; } = new();

    public List<AlarmSetting> AlarmSettings { get; set; } = new();

    public List<ActiveAlarm> Alarms { get; set; } = new();

    public List<ParameterLabel> ParameterLabels { get; set; } = new();

    public List<ParameterGroup> ParameterGroups { get; set; } = new();

    public AlarmSystemStatus? AlarmSystem { get; set; }

    public List<UmecObservation> Observations { get; set; } = new();
}

/// <summary>
/// ADT^A01 payload from the UDP beacon (ports 4600/4620): monitor identity
/// and state, plus patient demographics when someone is admitted.
/// </summary>
public sealed class DeviceAdtInfo
{
    /// <summary>Monitor identity and state, from the ADT beacon OBX segments.</summary>
    public MonitorInfo? Monitor { get; set; }

    /// <summary>Patient demographics from PID/PV1/EVN/OBX segments of the ADT beacon.</summary>
    public PatientInfo? Patient { get; set; }

    public List<UmecObservation> Observations { get; set; } = new();
}

/// <summary>
/// One OBX segment as sent by the monitor, text fields decoded. Every OBX of
/// a message is kept here, whether or not the parser interpreted it.
/// </summary>
public sealed class UmecObservation
{
    /// <summary>OBX-2: NM, CE, ST, TX, CD.</summary>
    public string ValueType { get; set; } = "";

    /// <summary>OBX-3 first component: parameter id or Mindray attribute code (2002, 2025, ...).</summary>
    public string Code { get; set; } = "";

    /// <summary>OBX-3 second component, decoded.</summary>
    public string Label { get; set; } = "";

    /// <summary>OBX-4: the parameter / module the observation refers to.</summary>
    public string SubId { get; set; } = "";

    /// <summary>OBX-5 as sent (components still separated by '^').</summary>
    public string Value { get; set; } = "";

    /// <summary>OBX-5 first component.</summary>
    public string ValueCode { get; set; } = "";

    /// <summary>OBX-5 second component, decoded.</summary>
    public string ValueText { get; set; } = "";

    /// <summary>OBX-6.</summary>
    public string Units { get; set; } = "";

    /// <summary>OBX-11, normally "F".</summary>
    public string Status { get; set; } = "";

    /// <summary>OBX-13, e.g. "APERIODIC".</summary>
    public string AccessCheck { get; set; } = "";

    /// <summary>OBX-14, monitor local time.</summary>
    public DateTime? Timestamp { get; set; }
}
