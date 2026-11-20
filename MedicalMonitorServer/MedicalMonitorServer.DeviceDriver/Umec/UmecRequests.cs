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
    /// <para>
    /// Mindray Realtime Results Interface (PDS Protocol Programmer's Guide, 6.9):
    /// QRD is required, and periodic parameters (ORU^R01 id 204: HR, SpO2, RR, Temp...)
    /// are only sent for a QRF filter that asks for them. A bare MSH still gets
    /// settings, alarms and aperiodic NIBP. Verified on a live uMec10 (SW 01.10.00):
    /// it sends 204 only once this query is repeated after the initial settings dump
    /// (see <see cref="UmecLinkOptions.QueryRefreshInterval"/>), and never in demo mode.
    /// QRF filter = &lt;IP&gt;&amp;&lt;IPSeq&gt;^&lt;SendType&gt;^&lt;SendFreq s&gt;^&lt;SendAll&gt;^&lt;List&gt;;
    /// IP/IPSeq are 0 for a direct monitor link. SendType 1 = parameters,
    /// 2 = waveforms, 3 = physiological alarms, 4 = technical alarms.
    /// QRD-1 only has to be a valid timestamp; QRD-4 is any id under 16 bytes.
    /// </para>
    /// <para>
    /// SendType 2 is "reserved" in revision 14 of the guide, although its own QRF example
    /// (6.9.5) still uses it with a list of 1101&amp;1151&amp;1152. Verified on a live uMec10
    /// (SW 01.10.00): with this line the monitor adds ORU^R01 id 157 once a second, one
    /// message per listed channel carrying that second's samples in a ZMW segment (see
    /// <c>ParserService</c>); without it no samples are sent at all. The list holds channel
    /// ids as in OBX 2153, here the three a station draws: 1101 = ECG channel 1 (lead II
    /// unless changed on the monitor), 1151 = respiration, 1152 = Pleth. "Send all" is
    /// defined for parameters only, so the channels are named.
    /// </para>
    /// </summary>
    public const string StartDataQueryText =
        "MSH|^~\\&|||||||QRY^R02|1203|P|2.3.1\r" +
        "QRD|20060731145557|R|I|MMSQRY|||||RES\r" +
        "QRF|MON||||0&0^1^1^1^\r" +
        "QRF|MON||||0&0^2^1^0^1101&1151&1152\r" +
        "QRF|MON||||0&0^3^1^1^\r" +
        "QRF|MON||||0&0^4^1^1^\r";

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
