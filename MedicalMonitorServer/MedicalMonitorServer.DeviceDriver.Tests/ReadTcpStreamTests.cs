using System.Net;
using System.Net.Sockets;
using System.Text;
using MedicalMonitorServer.DeviceDriver;

namespace MedicalMonitorServer.DeviceDriver.Tests;

/// <summary>
/// The real socket path, against a TCP listener on the loopback interface.
/// Everything else in this suite replaces the reader with a fake, so these are
/// the only tests that cover connecting, the read timeout, EOF, and the
/// session being cancelled when the stream stops.
/// </summary>
public class ReadTcpStreamTests : IDisposable
{
    private const byte VT = 0x0B;
    private const byte FS = 0x1C;
    private const byte CR = 0x0D;

    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);

    private const string Echo = "MSH|^~\\&|||||||ORU^R01|106|P|2.3.1|\r";

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();

    public ReadTcpStreamTests()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
    }

    private int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _cts.Dispose();
    }

    private Task<byte[]> Collect(List<byte[]> into, byte[] data)
    {
        lock (into) { into.Add(data); }
        return Task.FromResult(data);
    }

    private static byte[] Wire(string payload) =>
        ComunicationService.Frame(Encoding.Latin1.GetBytes(payload));

    [Fact]
    public async Task Messages_from_a_real_socket_are_delivered_unframed()
    {
        var received = new List<byte[]>();
        var server = Task.Run(async () =>
        {
            using var client = await _listener.AcceptTcpClientAsync(_cts.Token);
            await client.GetStream().WriteAsync(Wire("first"), _cts.Token);
            await client.GetStream().WriteAsync(Wire(Echo), _cts.Token);
            await Task.Delay(100, _cts.Token);
            client.Close();                 // EOF ends the read loop
        });

        var service = new ComunicationService();
        await service.ReadTcpStreamAsync(
            "127.0.0.1", Port,
            (data, _) => Collect(received, data),
            cancellationToken: _cts.Token).WaitAsync(Generous);

        await server.WaitAsync(Generous);
        Assert.Equal(
            new[] { "first", Echo },
            received.Select(r => Encoding.Latin1.GetString(r)));
    }

    [Fact]
    public async Task A_message_split_across_two_writes_arrives_as_one()
    {
        var received = new List<byte[]>();
        var wire = Wire(Echo);
        var server = Task.Run(async () =>
        {
            using var client = await _listener.AcceptTcpClientAsync(_cts.Token);
            var stream = client.GetStream();
            await stream.WriteAsync(wire.AsMemory(0, 10), _cts.Token);
            await stream.FlushAsync(_cts.Token);
            await Task.Delay(80, _cts.Token);               // force two reads
            await stream.WriteAsync(wire.AsMemory(10), _cts.Token);
            await Task.Delay(80, _cts.Token);
            client.Close();
        });

        var service = new ComunicationService();
        await service.ReadTcpStreamAsync(
            "127.0.0.1", Port,
            (data, _) => Collect(received, data),
            cancellationToken: _cts.Token).WaitAsync(Generous);

        await server.WaitAsync(Generous);
        Assert.Equal(Echo, Encoding.Latin1.GetString(Assert.Single(received)));
    }

    [Fact]
    public async Task What_the_session_sends_arrives_at_the_monitor_MLLP_framed()
    {
        var fromClient = new TaskCompletionSource<byte[]>();
        var server = Task.Run(async () =>
        {
            using var client = await _listener.AcceptTcpClientAsync(_cts.Token);
            var buffer = new byte[1024];
            int read = await client.GetStream().ReadAsync(buffer, _cts.Token);
            fromClient.TrySetResult(buffer[..read]);
            client.Close();
        });

        var service = new ComunicationService();
        await service.ReadTcpStreamAsync(
            "127.0.0.1", Port,
            (_, _) => Task.CompletedTask,
            async (link, token) => await link.SendAsync(
                Encoding.Latin1.GetBytes("HELLO"), token),
            _cts.Token).WaitAsync(Generous);

        var sent = await fromClient.Task.WaitAsync(Generous);
        await server.WaitAsync(Generous);
        Assert.Equal(new byte[] { VT, (byte)'H', (byte)'E', (byte)'L', (byte)'L', (byte)'O', FS, CR }, sent);
    }

    [Fact]
    public async Task The_session_is_cancelled_when_the_monitor_closes_the_link()
    {
        var sessionToken = new TaskCompletionSource<CancellationToken>();
        var server = Task.Run(async () =>
        {
            using var client = await _listener.AcceptTcpClientAsync(_cts.Token);
            await Task.Delay(80, _cts.Token);
            client.Close();                 // hang up
        });

        var service = new ComunicationService();
        CancellationToken captured = default;
        await service.ReadTcpStreamAsync(
            "127.0.0.1", Port,
            (_, _) => Task.CompletedTask,
            async (_, token) =>
            {
                captured = token;
                sessionToken.TrySetResult(token);
                await Task.Delay(Timeout.Infinite, token);
            },
            _cts.Token).WaitAsync(Generous);

        await server.WaitAsync(Generous);
        await sessionToken.Task.WaitAsync(Generous);
        Assert.True(captured.IsCancellationRequested,
            "the session must be cancelled once the stream is over");
    }

    [Fact]
    public async Task A_silent_monitor_fails_the_session_once_the_read_timeout_passes()
    {
        var server = Task.Run(async () =>
        {
            // Accept, then say nothing at all.
            using var client = await _listener.AcceptTcpClientAsync(_cts.Token);
            await Task.Delay(Generous, _cts.Token);
        });

        var service = new ComunicationService { ReadTimeout = TimeSpan.FromMilliseconds(150) };

        var failure = await Assert.ThrowsAsync<TimeoutException>(() =>
            service.ReadTcpStreamAsync(
                "127.0.0.1", Port,
                (_, _) => Task.CompletedTask,
                cancellationToken: _cts.Token).WaitAsync(Generous));

        Assert.Contains(Port.ToString(), failure.Message);
    }

    [Fact]
    public async Task A_monitor_that_keeps_talking_does_not_hit_the_read_timeout()
    {
        var received = new List<byte[]>();
        var server = Task.Run(async () =>
        {
            using var client = await _listener.AcceptTcpClientAsync(_cts.Token);
            for (int i = 0; i < 6; i++)
            {
                await client.GetStream().WriteAsync(Wire(Echo), _cts.Token);
                await Task.Delay(60, _cts.Token);           // under the timeout
            }

            client.Close();
        });

        // Shorter than the whole exchange, longer than the gap between writes.
        var service = new ComunicationService { ReadTimeout = TimeSpan.FromMilliseconds(200) };
        await service.ReadTcpStreamAsync(
            "127.0.0.1", Port,
            (data, _) => Collect(received, data),
            cancellationToken: _cts.Token).WaitAsync(Generous);

        await server.WaitAsync(Generous);
        Assert.Equal(6, received.Count);
    }

    [Fact]
    public async Task A_failure_in_the_session_ends_the_read()
    {
        var server = Task.Run(async () =>
        {
            using var client = await _listener.AcceptTcpClientAsync(_cts.Token);
            await Task.Delay(Generous, _cts.Token);         // stream stays open
        });

        var service = new ComunicationService();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ReadTcpStreamAsync(
                "127.0.0.1", Port,
                (_, _) => Task.CompletedTask,
                (_, _) => throw new InvalidOperationException("the session gave up"),
                _cts.Token).WaitAsync(Generous));
    }

    [Fact]
    public async Task Cancelling_stops_the_read()
    {
        var server = Task.Run(async () =>
        {
            using var client = await _listener.AcceptTcpClientAsync(_cts.Token);
            await Task.Delay(Generous, _cts.Token);
        });

        using var caller = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        var service = new ComunicationService();
        var reading = service.ReadTcpStreamAsync(
            "127.0.0.1", Port,
            (_, _) => Task.CompletedTask,
            cancellationToken: caller.Token);

        caller.CancelAfter(100);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading.WaitAsync(Generous));
    }

    [Fact]
    public async Task Connecting_to_a_port_nobody_is_listening_on_fails()
    {
        // Take a port, then release it, so nothing is behind it.
        var spare = new TcpListener(IPAddress.Loopback, 0);
        spare.Start();
        int deadPort = ((IPEndPoint)spare.LocalEndpoint).Port;
        spare.Stop();

        var service = new ComunicationService();

        await Assert.ThrowsAnyAsync<SocketException>(() =>
            service.ReadTcpStreamAsync(
                "127.0.0.1", deadPort,
                (_, _) => Task.CompletedTask,
                cancellationToken: _cts.Token).WaitAsync(Generous));
    }

    [Fact]
    public async Task Buffered_bytes_are_dropped_when_a_connection_starts()
    {
        var service = new ComunicationService();

        // A half-received frame left over from a previous link.
        service.Feed(new byte[] { VT, (byte)'o', (byte)'l', (byte)'d' });
        Assert.True(service.Pending > 0);

        var received = new List<byte[]>();
        var server = Task.Run(async () =>
        {
            using var client = await _listener.AcceptTcpClientAsync(_cts.Token);
            await client.GetStream().WriteAsync(Wire("fresh"), _cts.Token);
            await Task.Delay(100, _cts.Token);
            client.Close();
        });

        await service.ReadTcpStreamAsync(
            "127.0.0.1", Port,
            (data, _) => Collect(received, data),
            cancellationToken: _cts.Token).WaitAsync(Generous);

        await server.WaitAsync(Generous);

        // "old" must not be glued to the front of the first new frame.
        Assert.Equal("fresh", Encoding.Latin1.GetString(Assert.Single(received)));
    }
}
