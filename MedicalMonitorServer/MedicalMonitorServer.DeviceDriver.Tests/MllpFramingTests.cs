using System.Text;
using MedicalMonitorServer.DeviceDriver;
using MedicalMonitorServer.DeviceDriver.Umec;

namespace MedicalMonitorServer.DeviceDriver.Tests;

/// <summary>
/// Framing tests for <see cref="ComunicationService.Feed"/>. TCP hands the
/// reader arbitrary chunks, so the cases that matter are the ones where a
/// frame does not line up with a read: split anywhere, several per chunk, and
/// noise in between.
/// </summary>
public class MllpFramingTests
{
    private const byte VT = 0x0B;
    private const byte FS = 0x1C;
    private const byte CR = 0x0D;

    /// <summary>A real uMec10 message, so payloads under test look like the wire.</summary>
    private const string Echo = "MSH|^~\\&|||||||ORU^R01|106|P|2.3.1|\r";

    private static byte[] Bytes(string text) => Encoding.Latin1.GetBytes(text);

    private static string Text(byte[] payload) => Encoding.Latin1.GetString(payload);

    /// <summary>One message as it appears on the wire, MLLP framed.</summary>
    private static byte[] Wire(string payload) => ComunicationService.Frame(Bytes(payload));

    private static byte[] Concat(params byte[][] parts) =>
        parts.SelectMany(p => p).ToArray();

    // ---------------------------------------------------------------- Frame()

    [Fact]
    public void Frame_wraps_payload_in_VT_FS_CR()
    {
        var frame = ComunicationService.Frame(Bytes("abc"));

        Assert.Equal(new byte[] { VT, (byte)'a', (byte)'b', (byte)'c', FS, CR }, frame);
    }

    [Fact]
    public void Frame_of_empty_payload_is_just_the_framing_bytes()
    {
        Assert.Equal(new byte[] { VT, FS, CR }, ComunicationService.Frame([]));
    }

    [Fact]
    public void Frame_output_is_accepted_by_Feed()
    {
        var service = new ComunicationService();

        var frames = service.Feed(Wire(Echo));

        Assert.Equal(Echo, Text(Assert.Single(frames)));
        Assert.Equal(0, service.Pending);
    }

    [Fact]
    public void Both_outgoing_requests_round_trip_through_framing()
    {
        var service = new ComunicationService();

        var frames = service.Feed(Concat(
            ComunicationService.Frame(UmecRequests.StartDataQuery.Span),
            ComunicationService.Frame(UmecRequests.Echo.Span)));

        Assert.Equal(2, frames.Count);
        Assert.Equal(UmecRequests.StartDataQueryText, Text(frames[0]));
        Assert.Equal(UmecRequests.EchoText, Text(frames[1]));
    }

    // ------------------------------------------------------- one whole frame

    [Fact]
    public void A_complete_frame_yields_its_payload_without_the_framing_bytes()
    {
        var service = new ComunicationService();

        var payload = Assert.Single(service.Feed(Wire(Echo)));

        Assert.Equal(Echo, Text(payload));
        Assert.DoesNotContain(VT, payload);
        Assert.DoesNotContain(FS, payload);
    }

    [Fact]
    public void An_empty_frame_yields_an_empty_payload()
    {
        var service = new ComunicationService();

        var frames = service.Feed(new byte[] { VT, FS, CR });

        Assert.Empty(Assert.Single(frames));
        Assert.Equal(0, service.Pending);
    }

    [Fact]
    public void Payload_bytes_above_ASCII_survive_framing()
    {
        var service = new ComunicationService();
        var payload = new byte[] { 0x00, 0x7F, 0x80, 0xA0, 0xFF, 0x0A };

        var frames = service.Feed(ComunicationService.Frame(payload));

        Assert.Equal(payload, Assert.Single(frames));
    }

    // -------------------------------------------------------- split chunks

    public static TheoryData<int> EverySplitOfOneFrame()
    {
        var data = new TheoryData<int>();
        for (int i = 0; i <= Wire(Echo).Length; i++)
        {
            data.Add(i);
        }

        return data;
    }

    /// <summary>
    /// The frame is delivered in two reads that break at every possible byte,
    /// including before the VT and after the CR.
    /// </summary>
    [Theory]
    [MemberData(nameof(EverySplitOfOneFrame))]
    public void A_frame_split_across_two_reads_is_reassembled(int split)
    {
        var service = new ComunicationService();
        var wire = Wire(Echo);

        var first = service.Feed(wire.AsSpan(0, split));
        var second = service.Feed(wire.AsSpan(split));

        var frames = first.Concat(second).ToList();
        Assert.Equal(Echo, Text(Assert.Single(frames)));
        Assert.Equal(0, service.Pending);
    }

    [Fact]
    public void A_frame_fed_one_byte_at_a_time_is_reassembled()
    {
        var service = new ComunicationService();
        var wire = Wire(Echo);
        var frames = new List<byte[]>();

        foreach (var b in wire)
        {
            frames.AddRange(service.Feed(new[] { b }));
        }

        Assert.Equal(Echo, Text(Assert.Single(frames)));
        Assert.Equal(0, service.Pending);
    }

    public static TheoryData<int> EverySplitOfThreeFrames()
    {
        var data = new TheoryData<int>();
        for (int i = 0; i <= ThreeFrames().Length; i++)
        {
            data.Add(i);
        }

        return data;
    }

    private static byte[] ThreeFrames() =>
        Concat(Wire("first"), Wire(Echo), Wire("third"));

    /// <summary>Order must hold however the three frames are cut in two.</summary>
    [Theory]
    [MemberData(nameof(EverySplitOfThreeFrames))]
    public void Frames_keep_their_order_across_any_split(int split)
    {
        var service = new ComunicationService();
        var wire = ThreeFrames();

        var frames = service.Feed(wire.AsSpan(0, split))
            .Concat(service.Feed(wire.AsSpan(split)))
            .Select(Text)
            .ToList();

        Assert.Equal(new[] { "first", Echo, "third" }, frames);
        Assert.Equal(0, service.Pending);
    }

    // ------------------------------------------------ several frames at once

    [Fact]
    public void Back_to_back_frames_in_one_read_are_all_returned()
    {
        var service = new ComunicationService();

        var frames = service.Feed(Concat(Wire("a"), Wire("b"), Wire("c")));

        Assert.Equal(new[] { "a", "b", "c" }, frames.Select(Text));
        Assert.Equal(0, service.Pending);
    }

    [Fact]
    public void A_trailing_partial_frame_is_held_back_until_it_completes()
    {
        var service = new ComunicationService();

        var first = service.Feed(Concat(Wire("done"), Bytes($"{(char)VT}half")));

        Assert.Equal("done", Text(Assert.Single(first)));
        Assert.True(service.Pending > 0, "the partial frame must stay buffered");

        var second = service.Feed(new byte[] { FS, CR });

        Assert.Equal("half", Text(Assert.Single(second)));
        Assert.Equal(0, service.Pending);
    }

    // ------------------------------------------------------------ incomplete

    [Fact]
    public void A_frame_with_no_terminator_returns_nothing_and_stays_pending()
    {
        var service = new ComunicationService();

        Assert.Empty(service.Feed(Concat(new byte[] { VT }, Bytes(Echo))));
        Assert.Equal(Echo.Length + 1, service.Pending);
    }

    [Fact]
    public void An_empty_read_changes_nothing()
    {
        var service = new ComunicationService();
        service.Feed(Concat(new byte[] { VT }, Bytes("pending")));
        var pendingBefore = service.Pending;

        Assert.Empty(service.Feed([]));
        Assert.Equal(pendingBefore, service.Pending);
    }

    [Fact]
    public void Reset_drops_a_half_received_frame()
    {
        var service = new ComunicationService();
        service.Feed(Concat(new byte[] { VT }, Bytes("half")));
        Assert.True(service.Pending > 0);

        service.Reset();

        Assert.Equal(0, service.Pending);

        // The leftovers must not reappear on the front of the next frame.
        var frames = service.Feed(Wire("whole"));
        Assert.Equal("whole", Text(Assert.Single(frames)));
    }

    // ----------------------------------------------------------------- noise

    [Fact]
    public void Bytes_before_the_first_VT_are_discarded()
    {
        var service = new ComunicationService();

        var frames = service.Feed(Concat(Bytes("garbage"), Wire(Echo)));

        Assert.Equal(Echo, Text(Assert.Single(frames)));
        Assert.Equal(0, service.Pending);
    }

    [Fact]
    public void A_read_without_any_VT_is_dropped_rather_than_buffered()
    {
        var service = new ComunicationService();

        Assert.Empty(service.Feed(Bytes("not a frame at all")));
        Assert.Equal(0, service.Pending);
    }

    [Fact]
    public void A_stray_FS_before_a_frame_does_not_produce_a_phantom_payload()
    {
        var service = new ComunicationService();

        var frames = service.Feed(Concat(new byte[] { FS, CR }, Wire("real")));

        Assert.Equal("real", Text(Assert.Single(frames)));
    }

    [Fact]
    public void The_CR_that_ends_a_frame_is_not_carried_into_the_next_payload()
    {
        var service = new ComunicationService();

        var frames = service.Feed(Concat(Wire("one"), Wire("two")));

        Assert.Equal(new[] { "one", "two" }, frames.Select(Text));
        Assert.All(frames, f => Assert.DoesNotContain(CR, f));
    }

    // ------------------------------------------------------------ overflow

    [Fact]
    public void A_frame_that_never_ends_is_dropped_once_it_passes_MaxFrameSize()
    {
        var service = new ComunicationService { MaxFrameSize = 64 };

        Assert.Empty(service.Feed(Concat(new byte[] { VT }, new byte[200])));
        Assert.Equal(0, service.Pending);
    }

    [Fact]
    public void The_reader_recovers_after_dropping_an_oversized_frame()
    {
        var service = new ComunicationService { MaxFrameSize = 64 };
        service.Feed(Concat(new byte[] { VT }, new byte[200]));

        var frames = service.Feed(Wire("after"));

        Assert.Equal("after", Text(Assert.Single(frames)));
        Assert.Equal(0, service.Pending);
    }

    /// <summary>
    /// Characterisation: MaxFrameSize bounds how many bytes may sit in the
    /// buffer waiting for a terminator, it does not reject a frame that
    /// arrived complete. Change this test deliberately, not by accident.
    /// </summary>
    [Fact]
    public void A_complete_frame_larger_than_MaxFrameSize_is_still_delivered()
    {
        var service = new ComunicationService { MaxFrameSize = 64 };

        var frames = service.Feed(ComunicationService.Frame(new byte[200]));

        Assert.Equal(200, Assert.Single(frames).Length);
    }

    // -------------------------------------------------------- a real session

    /// <summary>
    /// What a live link actually looks like: the monitor's echo once a second,
    /// cut into reads that ignore frame boundaries entirely.
    /// </summary>
    [Fact]
    public void A_stream_of_echoes_in_uneven_reads_is_parsed_one_for_one()
    {
        var service = new ComunicationService();
        var wire = Concat(Enumerable.Range(0, 20).Select(_ => Wire(Echo)).ToArray());
        var frames = new List<byte[]>();

        // Read sizes that are coprime-ish with the frame length, so almost
        // every read straddles a boundary.
        int offset = 0;
        foreach (var size in new[] { 7, 13, 1, 29, 3, 61, 11 }.Repeat(20))
        {
            if (offset >= wire.Length) break;
            int take = Math.Min(size, wire.Length - offset);
            frames.AddRange(service.Feed(wire.AsSpan(offset, take)));
            offset += take;
        }

        Assert.Equal(20, frames.Count);
        Assert.All(frames, f => Assert.Equal(Echo, Text(f)));
        Assert.Equal(0, service.Pending);
    }
}

internal static class TestEnumerableExtensions
{
    /// <summary>Repeats a sequence <paramref name="times"/> over.</summary>
    public static IEnumerable<T> Repeat<T>(this IEnumerable<T> source, int times)
    {
        var cached = source.ToArray();
        for (int i = 0; i < times; i++)
        {
            foreach (var item in cached)
            {
                yield return item;
            }
        }
    }
}
