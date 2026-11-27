namespace MedicalMonitorServer.DeviceDriver;

/// <summary>What became of a station's request to start or stop a cuff measurement on a monitor.</summary>
public enum CuffRequestStatus
{
    /// <summary>
    /// The command is on its way to the monitor. Whether the cuff does as it was told shows in
    /// the monitor's next reports - a measurement running, or not - and nowhere else.
    /// </summary>
    Accepted,

    /// <summary>
    /// No driver here can tell this monitor to measure: a uMec, a Nihon Kohden that is on HL7,
    /// or an address nobody reads.
    /// </summary>
    NotSupported,

    /// <summary>The driver could, and has not been allowed to: the option is off.</summary>
    NotAllowed,

    /// <summary>The monitor is not streaming, and takes a command only from a node it streams to.</summary>
    NotStreaming,

    /// <summary>A start was asked for while the monitor reports a measurement running.</summary>
    AlreadyMeasuring,

    /// <summary>
    /// A start was asked for a moment after another one, before the monitor could have reported
    /// what it did with the first. Nothing was sent.
    /// </summary>
    TooSoon,
}

/// <summary>
/// Monitors whose cuff a station can start and stop over the network. This inflates a cuff on
/// a patient at somebody's word from another room, so a driver offers it only where it was
/// allowed in configuration, sends a command once and never repeats it by itself.
/// </summary>
public interface IRemoteCuff
{
    /// <summary>Whether a request for this monitor would be taken at all, streaming or not.</summary>
    bool CanControlCuff(string monitorIp);

    /// <summary>Asks the monitor to start a cuff measurement, or to stop the one that is running.</summary>
    CuffRequestStatus RequestCuff(string monitorIp, bool start);
}
