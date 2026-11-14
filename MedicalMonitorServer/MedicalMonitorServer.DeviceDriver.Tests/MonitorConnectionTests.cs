using MedicalMonitorServer.Contracts.Models;
using MedicalMonitorServer.DeviceDriver.Umec;
using Microsoft.Extensions.Logging.Abstractions;

namespace MedicalMonitorServer.DeviceDriver.Tests;

/// <summary>
/// The session and retry behaviour of one monitor link: the start query, the
/// keep-alive echo, reconnecting after a drop, and giving up.
/// </summary>
public class MonitorConnectionTests
{
    /// <summary>Real timings compressed so a whole retry sequence runs in milliseconds.</summary>
    private static UmecLinkOptions FastOptions => new()
    {
        ReconnectDelay = TimeSpan.FromMilliseconds(10),
        EchoInterval = TimeSpan.FromMilliseconds(20),
        MaxFailedSessions = 3,
    };

    /// <summary>An ORU the parser accepts, so a session counts as having carried data.</summary>
    private const string Vitals = "MSH|^~\\&|||||||ORU^R01|77|P|2.3.1|\rPID|||12345||DOE^JANE||19700101|F\r";

    private static DeviceState Monitor(string ip = "10.0.0.5", string port = "4601") => new()
    {
        Id = 1,
        Name = "uMec10",
        IpAddress = ip,
        Port = port,
        State = DeviceStateEnum.Connected,
    };

    private static MonitorConnection Build(
        FakeReader reader,
        out List<UmecMessage> received,
        out List<bool> closedWith,
        UmecLinkOptions? options = null,
        DeviceState? state = null)
    {
        var messages = new List<UmecMessage>();
        var closures = new List<bool>();
        received = messages;
        closedWith = closures;

        return new MonitorConnection(
            state ?? Monitor(),
            4601,
            message => { lock (messages) { messages.Add(message); } },
            (_, cancelled) => { lock (closures) { closures.Add(cancelled); } },
            reader,
            new ParserService(),
            options ?? FastOptions,
            NullLogger.Instance);
    }

    // ------------------------------------------------------------ the session

    [Fact]
    public async Task The_start_query_is_the_first_thing_sent_on_a_session()
    {
        var reader = new FakeReader(FakeReader.StaysOpen);
        using var connection = Build(reader, out _, out _);

        connection.Start();
        await Wait.UntilAsync(
            () => reader.Sessions.Count == 1 && reader.Sessions[0].Link.Sent.Count >= 1,
            "the connection sends something");

        Assert.Equal(UmecRequests.StartDataQueryText, reader.Sessions[0].Link.SentText[0]);
    }

    [Fact]
    public async Task The_echo_is_sent_repeatedly_while_the_stream_is_up()
    {
        var reader = new FakeReader(FakeReader.StaysOpen);
        using var connection = Build(reader, out _, out _);

        connection.Start();
        await Wait.UntilAsync(
            () => reader.Sessions.Count == 1 && reader.Sessions[0].Link.Sent.Count >= 4,
            "at least three echoes follow the start query");

        var sent = reader.Sessions[0].Link.SentText;
        Assert.Equal(UmecRequests.StartDataQueryText, sent[0]);
        Assert.All(sent.Skip(1), text => Assert.Equal(UmecRequests.EchoText, text));
    }

    [Fact]
    public async Task The_start_query_is_sent_again_on_every_reconnect()
    {
        var reader = new FakeReader(FakeReader.EndsImmediately);
        using var connection = Build(reader, out _, out _);

        connection.Start();
        await Wait.UntilAsync(() => reader.Attempts >= 3, "the connection has retried twice");

        Assert.All(reader.Sessions.Take(3),
            session => Assert.Equal(UmecRequests.StartDataQueryText, session.Link.SentText[0]));
    }

    [Fact]
    public async Task Sending_stops_when_the_stream_ends()
    {
        // One session that ends on its own, then a stream that stays up, so the
        // first session's writer has to have been cancelled rather than looping.
        var reader = new FakeReader(FakeReader.EndsImmediately, FakeReader.StaysOpen);
        using var connection = Build(reader, out _, out _);

        connection.Start();
        await Wait.UntilAsync(() => reader.Attempts >= 2, "the second session has started");

        var first = reader.Sessions[0];
        Assert.True(first.Token.IsCancellationRequested, "the ended session's token must be cancelled");

        var afterEnd = first.Link.Sent.Count;
        await Task.Delay(100);              // several echo intervals
        Assert.Equal(afterEnd, first.Link.Sent.Count);
    }

    // ------------------------------------------------------- the retry loop

    [Fact]
    public async Task A_stream_that_ends_is_retried()
    {
        var reader = new FakeReader(FakeReader.EndsImmediately);
        using var connection = Build(reader, out _, out _);

        connection.Start();
        await Wait.UntilAsync(() => reader.Attempts >= 2, "the connection dials again");
    }

    [Fact]
    public async Task A_connection_that_fails_outright_is_retried()
    {
        var reader = new FakeReader(FakeReader.Fails);
        using var connection = Build(reader, out _, out _);

        connection.Start();
        await Wait.UntilAsync(() => reader.Attempts >= 2, "a failed dial is retried");
    }

    [Fact]
    public async Task It_gives_up_after_MaxFailedSessions_fruitless_attempts()
    {
        var reader = new FakeReader(FakeReader.Fails);
        using var connection = Build(reader, out _, out var closedWith);

        connection.Start();
        await connection.Completion;

        Assert.Equal(FastOptions.MaxFailedSessions, reader.Attempts);
        Assert.Equal(new[] { false }, closedWith);      // closed by failure, not by us
    }

    [Fact]
    public async Task A_session_that_carried_data_resets_the_failure_count()
    {
        // Fail twice (one short of giving up), then deliver and drop, then fail
        // again: without the reset, the third failure would end it.
        var reader = new FakeReader(
            FakeReader.Fails,
            FakeReader.Fails,
            FakeReader.DeliversThenEnds(Vitals),
            FakeReader.Fails);
        using var connection = Build(reader, out var received, out var closedWith);

        connection.Start();
        await connection.Completion;

        // 2 fruitless + the good one + the 3 it then takes to give up. Without
        // the reset the loop would have stopped at 3 attempts.
        Assert.Equal(6, reader.Attempts);
        Assert.Single(received);
        Assert.Equal(new[] { false }, closedWith);
    }

    [Fact]
    public async Task Giving_up_reports_the_connection_closed_exactly_once()
    {
        var reader = new FakeReader(FakeReader.Fails);
        using var connection = Build(reader, out _, out var closedWith);

        connection.Start();
        await connection.Completion;
        await Task.Delay(50);

        Assert.Single(closedWith);
    }

    // ------------------------------------------------------------- shutdown

    [Fact]
    public async Task Disposing_ends_the_link_and_reports_it_as_our_own_doing()
    {
        var reader = new FakeReader(FakeReader.StaysOpen);
        var connection = Build(reader, out _, out var closedWith);

        connection.Start();
        await Wait.UntilAsync(() => reader.Attempts == 1, "the session is up");
        connection.Dispose();
        await connection.Completion;

        Assert.Equal(new[] { true }, closedWith);       // cancelled: we closed it
        Assert.Equal(1, reader.Attempts);               // no reconnect after disposal
    }

    [Fact]
    public async Task Disposing_twice_is_harmless()
    {
        var reader = new FakeReader(FakeReader.StaysOpen);
        var connection = Build(reader, out _, out var closedWith);

        connection.Start();
        await Wait.UntilAsync(() => reader.Attempts == 1, "the session is up");
        connection.Dispose();
        await connection.Completion;
        connection.Dispose();                           // after the loop disposed the CTS

        Assert.Single(closedWith);
    }

    [Fact]
    public async Task Disposing_during_the_reconnect_pause_stops_the_loop()
    {
        var reader = new FakeReader(FakeReader.EndsImmediately);
        var options = FastOptions with { ReconnectDelay = TimeSpan.FromSeconds(30) };
        var connection = Build(reader, out _, out var closedWith, options);

        connection.Start();
        await Wait.UntilAsync(() => reader.Attempts == 1, "the first session is over");
        connection.Dispose();

        // Must not sit out the 30s pause.
        await connection.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { true }, closedWith);
    }

    // -------------------------------------------------- delivered messages

    [Fact]
    public async Task A_delivered_message_is_parsed_and_handed_on()
    {
        var reader = new FakeReader(FakeReader.Delivers(Vitals));
        using var connection = Build(reader, out var received, out _);

        connection.Start();
        await Wait.UntilAsync(() => received.Count == 1, "the message arrives");

        Assert.Equal("ORU^R01", received[0].MessageType);
        Assert.Equal("12345", received[0].Oru?.Patient?.PatientId);
    }

    [Fact]
    public async Task Bytes_that_are_not_HL7_are_ignored_without_ending_the_session()
    {
        var reader = new FakeReader(FakeReader.Delivers("this is not HL7 at all"));
        using var connection = Build(reader, out var received, out _);

        connection.Start();
        await Wait.UntilAsync(
            () => reader.Sessions.Count == 1 && reader.Sessions[0].Link.Sent.Count >= 3,
            "the session carries on echoing");

        Assert.Empty(received);
        Assert.Equal(1, reader.Attempts);
    }

    [Fact]
    public async Task A_subscriber_that_throws_does_not_take_the_link_down()
    {
        var reader = new FakeReader(FakeReader.Delivers(Vitals, Vitals));
        int seen = 0;
        using var connection = new MonitorConnection(
            Monitor(),
            4601,
            _ =>
            {
                Interlocked.Increment(ref seen);
                throw new InvalidOperationException("subscriber is broken");
            },
            (_, _) => { },
            reader,
            new ParserService(),
            FastOptions,
            NullLogger.Instance);

        connection.Start();
        await Wait.UntilAsync(() => Volatile.Read(ref seen) >= 2, "both messages are offered");

        // Still the same session, still echoing.
        Assert.Equal(1, reader.Attempts);
        await Wait.UntilAsync(
            () => reader.Sessions[0].Link.Sent.Count >= 3, "the echo keeps going");
    }

    [Fact]
    public async Task The_monitor_is_dialled_at_the_address_from_its_device_state()
    {
        var reader = new FakeReader(FakeReader.StaysOpen);
        using var connection = Build(reader, out _, out _, state: Monitor("192.168.1.77"));

        connection.Start();
        await Wait.UntilAsync(() => reader.Attempts == 1, "the connection dials");

        Assert.Equal("192.168.1.77", reader.Sessions[0].Ip);
        Assert.Equal(4601, reader.Sessions[0].Port);
    }
}
