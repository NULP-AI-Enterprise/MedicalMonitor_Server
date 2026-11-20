using System.Text;
using MedicalMonitorServer.Contracts.Models;
using MedicalMonitorServer.DeviceDriver.Umec;

namespace MedicalMonitorServer.DeviceDriver.Tests;

/// <summary>
/// Waveform samples (ORU msg 157, segment ZMW). The real frames are in
/// Fixtures/umec_waveforms.hex: two consecutive seconds of channels 1101 (ECG II),
/// 1151 (respiration) and 1152 (Pleth), captured from a uMec10 on 2026-11-20.
/// </summary>
public class WaveformSamplesTests
{
    private static readonly ParserService Parser = new();

    private static readonly IReadOnlyList<byte[]> Frames = File
        .ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "umec_waveforms.hex"))
        .Select(line => line.Trim())
        .Where(line => line.Length > 0 && !line.StartsWith('#'))
        .Select(Convert.FromHexString)
        .ToList();

    private static WaveSamples Block(int frame) => Assert.Single(Parser.Parse(Frames[frame])!.Oru!.WaveSamples);

    [Fact]
    public void EveryCapturedFrame_IsAWaveformMessage_WithOneBlock()
    {
        Assert.Equal(6, Frames.Count);
        foreach (var frame in Frames)
        {
            var m = Parser.Parse(frame);
            Assert.NotNull(m);
            Assert.Equal(157, m!.MessageId);
            Assert.Equal(UmecMessageKind.Waveforms, m.Kind);
            Assert.Single(m.Oru!.WaveSamples);
            Assert.Empty(m.Oru.Vitals);
        }
    }

    [Theory]
    [InlineData(0, 1101, 256)]
    [InlineData(1, 1151, 128)]
    [InlineData(2, 1152, 256)]
    [InlineData(3, 1101, 256)]
    [InlineData(4, 1151, 128)]
    [InlineData(5, 1152, 256)]
    public void Block_NamesItsChannel_AndHoldsOneSecondOfSamples(int frame, int channelId, int samples)
    {
        var block = Block(frame);
        Assert.Equal(channelId, block.ChannelId);
        Assert.Equal(samples, block.Samples.Length);
    }

    [Fact]
    public void Samples_GetTheirTopBitBack()
    {
        // ECG idles around its zero line of 128, so neighbouring samples differ in the top
        // bit: a decoder that drops it turns 125, 129 into 125, 1.
        Assert.Equal(new byte[] { 125, 129, 125, 122, 121, 123, 127, 125 }, Block(0).Samples[..8]);
        Assert.Equal(new byte[] { 174, 174, 173, 173, 173, 172, 172, 172 }, Block(1).Samples[..8]);
        Assert.Equal(new byte[] { 42, 42, 42, 42, 42, 42, 42, 42 }, Block(2).Samples[..8]);
        Assert.Equal(new byte[] { 128, 131, 127, 119, 109, 100, 92, 80 }, Block(3).Samples[..8]);
    }

    [Theory]
    [InlineData(0, 66, 129)]    // ECG: a QRS complex down from the zero line
    [InlineData(1, 157, 184)]   // respiration: above its zero line of 128 for the whole second
    [InlineData(2, 34, 75)]     // Pleth: inside its 0..127 range, top bit never set
    [InlineData(4, 120, 190)]
    [InlineData(5, 37, 90)]
    public void Samples_StayInThePhysiologicalRangeOfTheCapture(int frame, int min, int max)
    {
        var samples = Block(frame).Samples;
        Assert.Equal(min, samples.Min());
        Assert.Equal(max, samples.Max());
    }

    [Fact]
    public void LastSamplesOfABlock_AreNotTakenFromThePadding()
    {
        // 256 samples and the 8-byte header leave two padding bytes in the last group.
        Assert.Equal(new byte[] { 128, 125, 123, 122, 126 }, Block(0).Samples[^5..]);
        // 128 samples leave four.
        Assert.Equal(new byte[] { 183, 184, 184 }, Block(1).Samples[^3..]);
    }

    [Fact]
    public void BytesAndLatin1String_GiveTheSameSamples()
    {
        var fromBytes = Parser.Parse(Frames[0])!;
        var fromString = Parser.Parse(Encoding.Latin1.GetString(Frames[0]))!;

        Assert.Equal(fromBytes.Oru!.WaveSamples[0].Samples, fromString.Oru!.WaveSamples[0].Samples);

        // Raw keeps the binary body byte for byte; only the segment terminator at the end is trimmed.
        Assert.Equal(Frames[0], Encoding.Latin1.GetBytes(fromBytes.Raw + "\r"));
    }

    [Fact]
    public void EveryByteValue_SurvivesThePacking()
    {
        var samples = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();

        var block = Assert.Single(Parser.Parse(Message(Pack(1101, samples)))!.Oru!.WaveSamples);

        Assert.Equal(1101, block.ChannelId);
        Assert.Equal(samples, block.Samples);
    }

    [Fact]
    public void SeveralZmwSegments_GiveSeveralBlocks()
    {
        var message = Message(Pack(1101, new byte[] { 1, 2, 3 }), Pack(1152, new byte[] { 200, 100 }));

        var blocks = Parser.Parse(message)!.Oru!.WaveSamples;

        Assert.Equal(new[] { 1101, 1152 }, blocks.Select(b => b.ChannelId));
        Assert.Equal(new byte[] { 200, 100 }, blocks[1].Samples);
    }

    public static TheoryData<string, byte[]> BrokenBodies()
    {
        var good = Pack(1101, Enumerable.Repeat((byte)128, 20).ToArray());

        var lowByte = (byte[])good.Clone();
        lowByte[9] = 0x41;                              // a wire byte without its top bit

        var wrongCount = Pack(1101, Enumerable.Repeat((byte)128, 20).ToArray(), declaredCount: 40);

        return new TheoryData<string, byte[]>
        {
            { "empty", Array.Empty<byte>() },
            { "cut short", good[..^3] },
            { "a byte below 0x80", lowByte },
            { "more samples declared than sent", wrongCount },
            { "no samples", Pack(1101, Array.Empty<byte>()) },
            { "twice as long as the count says", Pack(1101, Enumerable.Repeat((byte)128, 40).ToArray(), declaredCount: 20) },
        };
    }

    [Theory]
    [MemberData(nameof(BrokenBodies))]
    public void BodyThatDoesNotFitTheLayout_GivesNoSamples_AndDoesNotThrow(string what, byte[] body)
    {
        var m = Parser.Parse(Message(body));

        Assert.True(m is not null, what);
        Assert.Equal(UmecMessageKind.Waveforms, m!.Kind);
        Assert.Empty(m.Oru!.WaveSamples);
    }

    [Fact]
    public void StartQuery_AsksForTheThreeTracesAStationDraws()
    {
        Assert.Contains("QRF|MON||||0&0^2^1^0^1101&1151&1152\r", UmecRequests.StartDataQueryText);
    }

    private static byte[] Message(params byte[][] zmwBodies)
    {
        var bytes = new List<byte>(Encoding.Latin1.GetBytes("MSH|^~\\&|||||||ORU^R01|157|P|2.3.1|\r"));
        foreach (var body in zmwBodies)
        {
            bytes.AddRange(Encoding.Latin1.GetBytes("ZMW|"));
            bytes.AddRange(body);
            bytes.Add((byte)'\r');
        }

        return bytes.ToArray();
    }

    /// <summary>
    /// Packs a block the way the monitor does, written from the captured frames and
    /// independently of the parser: 8-byte header, samples, zero padding, then every seven
    /// bytes followed by an eighth that carries their top bits.
    /// </summary>
    private static byte[] Pack(int channelId, byte[] samples, int? declaredCount = null)
    {
        int count = declaredCount ?? samples.Length;
        var payload = new List<byte>
        {
            0xAB, 0xCD,                                 // the two bytes that differ in every block
            (byte)(channelId >> 8), (byte)channelId,
            1,
            (byte)(count >> 8), (byte)count,
            0,
        };
        payload.AddRange(samples);
        while (payload.Count % 7 != 0) payload.Add(0);

        var wire = new List<byte>();
        for (int at = 0; at < payload.Count; at += 7)
        {
            int topBits = 0x80;
            for (int i = 0; i < 7; i++)
            {
                wire.Add((byte)(payload[at + i] | 0x80));
                if ((payload[at + i] & 0x80) != 0) topBits |= 1 << i;
            }

            wire.Add((byte)topBits);
        }

        return wire.ToArray();
    }
}
