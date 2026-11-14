using System.Text;

namespace MedicalMonitorServer.DeviceDriver.Umec;

/// <summary>
/// The messages the server sends to a uMec10 over the HL7 link (TCP 4601).
/// Both follow the shape the monitor itself puts on the wire: an MSH segment
/// with every optional field empty, Mindray's message id in MSH-10, HL7 2.3.1,
/// segment terminated by CR. MLLP framing is added when sending, see
/// <see cref="IMonitorLink"/>.
/// </summary>
public static class UmecRequests
{
    /// <summary>
    /// QRY^R02, message id 1203: "start sending". Until the monitor gets it,
    /// it only emits its own echo and a few status messages, so this is the
    /// first thing sent on every connection.
    /// </summary>
    public const string StartDataQueryText = "MSH|^~\\&|||||||QRY^R02|1203|P|2.3.1|\r";

    /// <summary>
    /// ORU^R01, message id 106: the echo that tells the monitor we are still
    /// listening. It expects one at least every 10 seconds and the monitor
    /// sends the same message once a second in the other direction.
    /// </summary>
    public const string EchoText = "MSH|^~\\&|||||||ORU^R01|106|P|2.3.1|\r";

    /// <summary>Wire bytes of <see cref="StartDataQueryText"/>.</summary>
    public static ReadOnlyMemory<byte> StartDataQuery { get; } =
        Encoding.Latin1.GetBytes(StartDataQueryText);

    /// <summary>Wire bytes of <see cref="EchoText"/>.</summary>
    public static ReadOnlyMemory<byte> Echo { get; } =
        Encoding.Latin1.GetBytes(EchoText);
}
