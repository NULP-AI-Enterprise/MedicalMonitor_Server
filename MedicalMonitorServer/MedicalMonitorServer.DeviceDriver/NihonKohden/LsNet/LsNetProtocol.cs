using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace MedicalMonitorServer.DeviceDriver.NihonKohden.LsNet;

/// <summary>What kind of LS-NET datagram a <see cref="LsNetDatagram"/> is.</summary>
internal enum LsNetKind
{
    /// <summary>A node announcing itself (kind 04 FF, sent to everybody).</summary>
    Beacon,

    /// <summary>The answer to a request (kind 02 0x), carrying the request's sequence number.</summary>
    Response,

    /// <summary>A notification (kind 05 FF) that wants the bare acknowledgement back.</summary>
    Notification,

    /// <summary>Waveform samples, once a second with the numeric values behind them (02 0B).</summary>
    Waveforms,

    /// <summary>Numeric values on their own (02 12), what a numerics-only subscription gets.</summary>
    Numerics,

    /// <summary>Anything else: requests from another node, acknowledgements.</summary>
    Other,
}

/// <param name="Code">Nihon Kohden parameter code as its two wire bytes, high first (0x0700 = SpO2).</param>
/// <param name="Value">Whole number as sent; null when the monitor has no value (0x8000).</param>
/// <param name="Alarm">
/// The monitor's own limit alarm on the value, third status byte of the record:
/// <see cref="LsNetAlarm.None"/> while it is within its limits.
/// </param>
/// <param name="Sounding">
/// First status byte, bit 0x40: set when the alarm starts, cleared by Silence Alarms on the
/// monitor while <paramref name="Alarm"/> stays as it is.
/// </param>
/// <param name="State">
/// Fifth status byte: the technical state of the parameter, read like <see cref="LsNetState"/>.
/// 0x40 when there is nothing to say, 0x45 with a message code behind it while a cuff
/// measurement runs (code 0x1E) or the parameter is switched off (0x21).
/// </param>
/// <param name="StateCode">Sixth status byte: which message <paramref name="State"/> is about.</param>
internal readonly record struct LsNetNumeric(
    ushort Code, int? Value, byte Alarm = LsNetAlarm.None, bool Sounding = false, byte State = LsNetAlarm.None, byte StateCode = 0);

/// <summary>
/// A state byte of the numeric block: the alarm level in the high half, as in
/// <see cref="LsNetAlarm"/>, and the kind in the low half. Seen: 0x40 nothing, 0x24 a technical
/// alarm at the advisory level ("check electrodes", shown in blue), 0x45 a message that is not
/// an alarm. The monitor's summary of its technical state is the second of four such pairs in
/// the block header, each a state byte and a message code.
/// </summary>
internal static class LsNetState
{
    /// <summary>The alarm level of a state byte; <see cref="LsNetAlarm.None"/> for a mere message.</summary>
    public static byte LevelOf(byte state) => (byte)(state & 0xF0);

    /// <summary>The one message code whose meaning is known: an ECG electrode is off.</summary>
    public const byte CheckElectrodes = 0x01;
}

/// <summary>
/// Values of the alarm byte of a numeric record. Read off a BSM-3562 while its limits were moved
/// to raise alarms: the same alarm went from <see cref="Warning"/> to <see cref="Crisis"/> the
/// moment its priority was changed on the monitor from the yellow level to the red one, and a
/// temperature alarm set to the blue level came as <see cref="Advisory"/>. These are the
/// monitor's three priorities.
/// </summary>
internal static class LsNetAlarm
{
    public const byte Crisis = 0x00;
    public const byte Warning = 0x10;
    public const byte Advisory = 0x20;
    public const byte None = 0x40;
}

/// <param name="Code">Waveform code: 0x0100-0x010B the ECG leads I..V6, 0x0700 pleth, 0x2C00 arterial pressure.</param>
internal sealed record LsNetWave(ushort Code, short[] Samples);

/// <summary>One decoded datagram. Only the members of its <see cref="Kind"/> are filled.</summary>
internal sealed class LsNetDatagram
{
    public LsNetKind Kind { get; init; }

    /// <summary>Request type answered (Response) or announced (Notification): bytes 12-13.</summary>
    public ushort Type { get; init; }

    /// <summary>Sequence number of the request a Response answers, or of a Waveforms packet in its stream.</summary>
    public ushort Sequence { get; init; }

    /// <summary>Beacon: the node's id (address bytes 2-4 and 0x20). Requests name their target by it.</summary>
    public byte[] NodeId { get; init; } = Array.Empty<byte>();

    /// <summary>Beacon: the bed name set on the monitor.</summary>
    public string BedName { get; init; } = "";

    /// <summary>Response to the patient request (type 0x0309): the patient id, "" when the monitor has none.</summary>
    public string PatientId { get; init; } = "";

    public IReadOnlyList<LsNetWave> Waves { get; init; } = Array.Empty<LsNetWave>();

    /// <summary>Null when the datagram carries no numeric block (or one in a layout not seen before).</summary>
    public IReadOnlyList<LsNetNumeric>? Numerics { get; init; }

    /// <summary>With <see cref="Numerics"/>: the monitor's technical state, see <see cref="LsNetState"/>.</summary>
    public byte TechnicalState { get; init; } = LsNetAlarm.None;

    /// <summary>With <see cref="Numerics"/>: the message code of <see cref="TechnicalState"/>.</summary>
    public byte TechnicalCode { get; init; }
}

/// <summary>
/// The wire format of LS-NET, Nihon Kohden's own monitor network protocol, as far as a viewer
/// of one bed needs it. Nothing here comes from the manufacturer: it was worked out on a live
/// BSM-3562 (SW 07-41, "LS-NET 1st generation") on 2026-11-21 by posing as a second bed and
/// playing the monitor's own Interbed requests back at it (captures/nihon-kohden-lsnet-2026-11-21).
/// <para>
/// Everything is UDP. Control datagrams go to port 8300 from port 55405, in both directions;
/// the streams a viewer subscribes to arrive on its port 8301. A control datagram is
/// <code>
/// 0-1   kind: 00 0x request, 02 0x response, 04 FF beacon, 05 FF notification, 06 FF bare ack
/// 2-3   sequence number (a response repeats the request's)
/// 4-5   length of the whole datagram
/// 6-11  00 01 00 01 00 00
/// 12-13 type; a response's is the request's plus one
/// 16-17 length minus 12
/// 24-27 node id: in a request the target's, otherwise the sender's
/// 28-31 sender's IPv4 address
/// 32-   payload
/// </code>
/// </para>
/// </summary>
internal static class LsNetProtocol
{
    public const int ControlPort = 8300;
    public const int StreamPort = 8301;

    /// <summary>The port every control datagram of a BSM-3562 comes from; ours do the same.</summary>
    public const int ControlSourcePort = 55405;

    /// <summary>Every waveform is sent at this rate, 32 samples a packet.</summary>
    public const int SamplesPerSecond = 125;

    public const ushort SubscribeType = 0x0222;
    public const ushort PatientType = 0x0308;

    /// <summary>Last byte of the node id of a bedside monitor.</summary>
    private const byte BedNode = 0x20;

    private const int PatientIdOffset = 382;
    private const int PatientIdLength = 16;

    /// <summary>"No value" in a numeric record.</summary>
    private const short NoValue = unchecked((short)0x8000);

    // Closes every subscription request the BSM-3562 sends; what the two words select is not
    // known, and the monitor answers the same whatever they are.
    private static readonly byte[] SubscribeTail =
    {
        0x01, 0x00, 0x00, 0x00, 0x55, 0x55, 0x55, 0x7F,
        0x01, 0x00, 0x00, 0x00, 0x55, 0x55, 0x55, 0x57,
    };

    /// <summary>What a notification is answered with.</summary>
    public static readonly byte[] BareAcknowledgement =
        { 0x06, 0xFF, 0x00, 0x00, 0x00, 0x0C, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00 };

    /// <summary>A node's id as the BSM-3562 forms its own: the last three address bytes and 0x20.</summary>
    public static byte[] NodeId(IPAddress address)
    {
        var a = address.GetAddressBytes();
        return new byte[] { a[1], a[2], a[3], BedNode };
    }

    /// <summary>
    /// The 64-byte announcement of a node. The monitor only serves a node it hears this from:
    /// it ignores the requests of a silent one, and stops a stream about eight seconds after
    /// the beacons stop.
    /// <para>
    /// The last byte of the node id says what the node is. A BSM-3562 sends 0x20, and a node
    /// announcing 0x20 is a bed to it: listed in its Interbed window under
    /// <paramref name="bedName"/> and polled about once a second for its own values - which
    /// we do not have, so it shows as a bed whose monitor is off. With 0x00 the monitor
    /// streams just the same and neither lists nor polls the node (two minutes watched, no
    /// request, nothing new on its screen). What 0x00 means to Nihon Kohden is not known; 0x10
    /// and 0x40 behaved the same in a shorter run.
    /// </para>
    /// </summary>
    public static byte[] Beacon(string bedName, IPAddress self, bool asBed = false)
    {
        var a = self.GetAddressBytes();
        var b = new byte[64];
        new byte[] { 0x04, 0xFF, 0x00, 0x00, 0x00, 0x40, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0x02, 0x01, 0x00, 0x00, 0x00, 0x34 }.CopyTo(b, 0);
        b[24] = a[1]; b[25] = a[2]; b[26] = a[3];
        a.CopyTo(b, 28);
        b[33] = 0x01;
        Encoding.ASCII.GetBytes(bedName.Length > 11 ? bedName[..11] : bedName).CopyTo(b, 36);
        a.CopyTo(b, 48);
        b[52] = a[1]; b[53] = a[2]; b[54] = a[3]; b[55] = asBed ? BedNode : (byte)0x00;
        b[60] = 0x01; b[61] = 0x86; b[62] = 0x01;
        return b;
    }

    /// <summary>
    /// Request 0222: start sending the listed waveforms, and with them the numeric values once a
    /// second. A waveform the monitor does not have at the moment is left out of the stream;
    /// the rest still comes.
    /// </summary>
    public static byte[] Subscribe(ushort sequence, byte[] targetId, IPAddress self, IReadOnlyList<ushort> waveCodes)
    {
        var payload = new byte[8 + 4 * waveCodes.Count + SubscribeTail.Length];
        payload[0] = 0x04;
        payload[6] = (byte)waveCodes.Count;
        for (int i = 0; i < waveCodes.Count; i++)
        {
            payload[8 + 4 * i] = 0x08;
            BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(10 + 4 * i), waveCodes[i]);
        }

        SubscribeTail.CopyTo(payload, 8 + 4 * waveCodes.Count);
        return Request(SubscribeType, sequence, targetId, self, payload);
    }

    /// <summary>Request 0222 with an empty selection: what the monitor itself sends to end a stream.</summary>
    public static byte[] Unsubscribe(ushort sequence, byte[] targetId, IPAddress self) =>
        Request(SubscribeType, sequence, targetId, self, new byte[16]);

    /// <summary>Request 0308: who is the patient. Answered with type 0309.</summary>
    public static byte[] PatientQuery(ushort sequence, byte[] targetId, IPAddress self) =>
        Request(PatientType, sequence, targetId, self, Array.Empty<byte>(), kindLow: 0x01);

    private static byte[] Request(ushort type, ushort sequence, byte[] targetId, IPAddress self, byte[] payload, byte kindLow = 0x00)
    {
        int length = 32 + payload.Length;
        var b = new byte[length];
        b[1] = kindLow;
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(2), sequence);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(4), (ushort)length);
        b[7] = 0x01; b[9] = 0x01;
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(12), type);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(16), (ushort)(length - 12));
        targetId.CopyTo(b, 24);
        self.GetAddressBytes().CopyTo(b, 28);
        payload.CopyTo(b, 32);
        return b;
    }

    /// <summary>Decodes one datagram; null when it is too short to be LS-NET or its length field disagrees.</summary>
    public static LsNetDatagram? Parse(ReadOnlySpan<byte> d)
    {
        if (d.Length < 12 || BinaryPrimitives.ReadUInt16BigEndian(d[4..]) != d.Length) return null;

        // The two stream kinds have their own, shorter header.
        if (d[0] == 0x02 && d[1] == 0x0B) return ParseWaveforms(d);
        if (d[0] == 0x02 && d[1] == 0x12)
        {
            if (d.Length < 26) return null;

            var numerics = ParseNumerics(d, 24, out byte state, out byte code);
            return new LsNetDatagram { Kind = LsNetKind.Numerics, Numerics = numerics, TechnicalState = state, TechnicalCode = code };
        }

        if (d.Length < 32) return new LsNetDatagram { Kind = LsNetKind.Other };

        ushort type = BinaryPrimitives.ReadUInt16BigEndian(d[12..]);
        ushort sequence = BinaryPrimitives.ReadUInt16BigEndian(d[2..]);

        switch (d[0])
        {
            case 0x04 when d.Length == 64 && type == 0x0201:
                return new LsNetDatagram
                {
                    Kind = LsNetKind.Beacon,
                    Type = type,
                    NodeId = d.Slice(52, 4).ToArray(),
                    BedName = Text(d.Slice(36, 12)),
                };

            case 0x02:
                return new LsNetDatagram
                {
                    Kind = LsNetKind.Response,
                    Type = type,
                    Sequence = sequence,
                    // In the 446-byte answer the id sits at byte 382, NUL padded; the monitor
                    // keeps 16 characters of it. Nothing else in the answer is understood.
                    PatientId = type == PatientType + 1 && d.Length >= PatientIdOffset + PatientIdLength
                        ? Text(d.Slice(PatientIdOffset, PatientIdLength))
                        : "",
                };

            case 0x05:
                return new LsNetDatagram { Kind = LsNetKind.Notification, Type = type };

            default:
                return new LsNetDatagram { Kind = LsNetKind.Other, Type = type, Sequence = sequence };
        }
    }

    /// <summary>
    /// <code>
    /// 0-19  02 0B 00 00, length, six zeros, node id, address
    /// 22    number of waveform blocks
    /// 23    1 when the numeric block follows the waveforms
    /// 24-25 sequence number of the packet in the stream
    /// 28    offset of a detected beat in the packet, FF when none
    /// 36-   the blocks: code (2), 08, number of samples, then the samples, signed 16-bit big endian
    /// </code>
    /// Null for anything that does not fit this exactly: a trace is not drawn from a layout nobody has seen.
    /// </summary>
    private static LsNetDatagram? ParseWaveforms(ReadOnlySpan<byte> d)
    {
        if (d.Length < 40) return null;

        int blocks = d[22];
        var waves = new List<LsNetWave>(blocks);
        int at = 36;
        for (int i = 0; i < blocks; i++)
        {
            if (at + 4 > d.Length || d[at + 2] != 0x08) return null;

            int count = d[at + 3];
            if (at + 4 + 2 * count > d.Length) return null;

            var samples = new short[count];
            for (int k = 0; k < count; k++)
            {
                samples[k] = BinaryPrimitives.ReadInt16BigEndian(d[(at + 4 + 2 * k)..]);
            }

            waves.Add(new LsNetWave(BinaryPrimitives.ReadUInt16BigEndian(d[at..]), samples));
            at += 4 + 2 * count;
        }

        byte technicalState = LsNetAlarm.None, technicalCode = 0;
        var numerics = d[23] == 0x01 ? ParseNumerics(d, at, out technicalState, out technicalCode) : null;

        return new LsNetDatagram
        {
            Kind = LsNetKind.Waveforms,
            Sequence = BinaryPrimitives.ReadUInt16BigEndian(d[24..]),
            Waves = waves,
            Numerics = numerics,
            TechnicalState = technicalState,
            TechnicalCode = technicalCode,
        };
    }

    /// <summary>
    /// The numeric block: a count and that many waveform codes the monitor can stream, 36 bytes
    /// not understood, a count, and that many 10-byte records - code (2), status (6), value
    /// (signed 16-bit big endian). The records must end exactly where the datagram does.
    /// Of the six status bytes of a record the first, third, fifth and sixth are read (see
    /// <see cref="LsNetNumeric"/>). Of the 36 bytes, the last eight are four pairs of a state
    /// byte and a message code; the second pair is the monitor's technical alarm, the fourth
    /// repeats the highest limit alarm of the records.
    /// </summary>
    private static List<LsNetNumeric>? ParseNumerics(ReadOnlySpan<byte> d, int at, out byte technicalState, out byte technicalCode)
    {
        technicalState = LsNetAlarm.None;
        technicalCode = 0;
        if (at + 2 > d.Length) return null;

        int countAt = at + 2 + 2 * d[at] + 36;
        if (countAt + 2 > d.Length) return null;

        int count = d[countAt];
        int records = countAt + 2;
        if (records + 10 * count != d.Length) return null;

        technicalState = d[countAt - 6];
        technicalCode = d[countAt - 5];

        var numerics = new List<LsNetNumeric>(count);
        for (int i = 0; i < count; i++)
        {
            var record = d.Slice(records + 10 * i, 10);
            short value = BinaryPrimitives.ReadInt16BigEndian(record[8..]);
            numerics.Add(new LsNetNumeric(
                BinaryPrimitives.ReadUInt16BigEndian(record),
                value == NoValue ? null : value,
                Alarm: record[4],
                Sounding: (record[2] & 0x40) != 0,
                State: record[6],
                StateCode: record[7]));
        }

        return numerics;
    }

    /// <summary>NUL-padded ASCII; "" when it holds anything that is not printable.</summary>
    private static string Text(ReadOnlySpan<byte> field)
    {
        int end = field.IndexOf((byte)0);
        if (end >= 0) field = field[..end];

        foreach (byte c in field)
        {
            if (c is < 0x20 or > 0x7E) return "";
        }

        return Encoding.ASCII.GetString(field).Trim();
    }
}
