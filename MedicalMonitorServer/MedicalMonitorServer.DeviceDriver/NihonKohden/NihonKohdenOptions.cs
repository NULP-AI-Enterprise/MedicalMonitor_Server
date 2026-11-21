namespace MedicalMonitorServer.DeviceDriver.NihonKohden;

/// <summary>One Nihon Kohden bedside monitor switched to the HL7 network protocol.</summary>
public sealed class NihonKohdenMonitor
{
    /// <summary>Shown in the device list; the address is used when empty.</summary>
    public string Name { get; set; } = "";

    public string Ip { get; set; } = "";

    /// <summary>The monitor's "ORU port" (SYSTEM CONFIGURATION, NETWORK, HL7 page); 7998 from the factory.</summary>
    public int Port { get; set; } = 7998;

    /// <summary>
    /// The monitor's admit/discharge port (ADT^A04), 9005 from the factory. Open only while
    /// "USE ADT" is on in the monitor's HL7 settings; 0 never writes a patient to this monitor.
    /// </summary>
    public int AdmitPort { get; set; } = 9005;

    /// <summary>The monitor's patient information update port (ADT^A08), 9006 from the factory.</summary>
    public int PatientInfoPort { get; set; } = 9006;

    /// <summary>
    /// Which protocol is selected on the monitor (SYSTEM CONFIGURATION, NETWORK): "HL7" - one
    /// report a minute, no waveforms, the patient can be written to the monitor - or "LsNet" -
    /// values every second and waveforms, nothing can be written. The monitor runs one or the
    /// other; <see cref="Port"/> and the ADT ports mean nothing in LS-NET.
    /// </summary>
    public string Protocol { get; set; } = "HL7";

    public bool IsLsNet => string.Equals(Protocol, "LsNet", StringComparison.OrdinalIgnoreCase)
                           || string.Equals(Protocol, "LS-NET", StringComparison.OrdinalIgnoreCase);
}

/// <summary>How this PC appears and what it asks for on the monitors' LS-NET network.</summary>
public sealed class LsNetOptions
{
    /// <summary>
    /// The address this PC has on the monitor network (10.x.x.x with the monitors' automatic
    /// addressing). Required: the monitors are told it in every datagram.
    /// </summary>
    public string LocalIp { get; set; } = "";

    /// <summary>
    /// The name this PC announces itself under, 11 characters at most. A monitor only talks to
    /// a node that announces itself. Monitors show the name only with <see cref="AnnounceAsBed"/>.
    /// </summary>
    public string BedName { get; set; } = "STATION";

    /// <summary>
    /// False (the default) announces this PC as a node the monitors stream to without listing
    /// it. True announces it as a bedside monitor, as the first version of the driver did: the
    /// monitors then list it in their Interbed window and keep asking it for values it does
    /// not have. Only worth turning on if a monitor refuses the default.
    /// </summary>
    public bool AnnounceAsBed { get; set; }

    /// <summary>
    /// Waveforms to ask every monitor for, as their codes: "0100".."010B" the ECG leads I..V6,
    /// "0700" pleth. Empty asks for ECG II and pleth. One the monitor cannot give is left out.
    /// </summary>
    public List<string> Waveforms { get; set; } = new();

    /// <summary>
    /// Counts of an ECG sample per millivolt. NOT measured: 400 is an estimate from the
    /// monitor's screen (trace against its 0.25 mV bar). Check it against a 1 mV calibration
    /// signal before reading amplitudes off a station.
    /// </summary>
    public double EcgCountsPerMillivolt { get; set; } = 400;

    /// <summary>A monitor forgets a node about eight seconds after its last announcement.</summary>
    public int BeaconIntervalMs { get; set; } = 3000;

    /// <summary>Silence after which a monitor counts as disconnected (a packet comes every quarter second).</summary>
    public int StreamTimeoutMs { get; set; } = 5000;
}

/// <summary>
/// The "NihonKohden" section of appsettings. Monitors are listed by address and not found
/// by the watchdog: a probe that connects and hangs up makes the monitor drop the HL7
/// connection that follows it.
/// </summary>
public sealed class NihonKohdenOptions
{
    public List<NihonKohdenMonitor> Monitors { get; set; } = new();

    /// <summary>Used for the monitors whose <see cref="NihonKohdenMonitor.Protocol"/> is "LsNet".</summary>
    public LsNetOptions LsNet { get; set; } = new();

    /// <summary>
    /// Pause before dialling a monitor again. Seen on a BSM-3562 (SW 07-41): a connection
    /// opened right after another one was closed is dropped within 3 s; 8 s later it holds.
    /// </summary>
    public int ReconnectDelayMs { get; set; } = 10_000;

    /// <summary>
    /// Silence after which a link counts as dead. The monitor reports once a minute at the
    /// fastest, so this has to stay well above 60.
    /// </summary>
    public int ReadTimeoutSeconds { get; set; } = 180;

    /// <summary>
    /// The "ORU sampling" interval set on the monitors' HL7 page, in seconds: 60 is the shortest
    /// it offers. A monitor on HL7 that has reported within one and a half of these is not
    /// treated as silent by the server, however short Health:StaleAfterSeconds is.
    /// </summary>
    public int ReportIntervalSeconds { get; set; } = 60;
}
