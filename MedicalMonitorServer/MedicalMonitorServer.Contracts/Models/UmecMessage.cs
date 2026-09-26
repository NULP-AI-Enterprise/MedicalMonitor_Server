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
/// One decoded HL7 message from a Mindray uMec10: ORU^R01 from the TCP
/// stream or ADT^A01 from the UDP beacon.
/// The typed collections are always non-null; a message usually fills only
/// one of them. <see cref="Observations"/> keeps every OBX segment, including
/// the ones the parser does not interpret, so nothing from the feed is lost.
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

    public PatientInfo? Patient { get; set; }

    /// <summary>Monitor identity and state, from the ADT beacon.</summary>
    public MonitorInfo? Monitor { get; set; }

    public List<VitalSign> Vitals { get; set; } = new();

    public List<AlarmSetting> AlarmSettings { get; set; } = new();

    public List<ActiveAlarm> Alarms { get; set; } = new();

    public List<ParameterLabel> ParameterLabels { get; set; } = new();

    public List<ParameterGroup> ParameterGroups { get; set; } = new();

    public AlarmSystemStatus? AlarmSystem { get; set; }

    public List<UmecObservation> Observations { get; set; } = new();

    /// <summary>
    /// The message as received, without MLLP framing, segments separated by CR.
    /// Bytes are carried as Latin-1 chars (one char per byte), so
    /// <c>Encoding.Latin1.GetBytes(Raw)</c> gives back the exact wire bytes.
    /// </summary>
    public string Raw { get; set; } = "";
}
