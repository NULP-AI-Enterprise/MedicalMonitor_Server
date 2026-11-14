using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MedicalMonitorServer.DeviceDriver;

/// <summary>
/// The write side of a live monitor connection, handed to the caller for as
/// long as the stream is being read. Payloads go out on the same socket the
/// stream is read from, MLLP framed; sends are serialized, so the keep-alive
/// echo and a query may be sent from different tasks.
/// </summary>
public interface IMonitorLink
{
    /// <summary>Send one HL7 message (without MLLP framing bytes).</summary>
    Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default);
}

/// <summary>
/// Reader side of the monitor link. The uMec10 HL7 stream (TCP 4601) is MLLP
/// framed: &lt;VT&gt; message &lt;FS&gt; &lt;CR&gt;. TCP delivers it in arbitrary chunks, so
/// <see cref="Feed"/> collects bytes until a frame is complete and hands out
/// whole messages, which the parser then decodes. One instance per connection.
/// (UDP ADT beacons arrive one message per datagram and need no framing.)
/// </summary>
public interface IComunicationService
{
    int MaxFrameSize { get; }
    int Pending { get; }
    List<byte[]> Feed(ReadOnlySpan<byte> data);
    void Reset();

    Task ReadTcpStreamAsync(
        string ip,
        int port,
        Func<byte[], CancellationToken, Task> onMessageReceived,
        Func<IMonitorLink, CancellationToken, Task>? onConnected = null,
        CancellationToken cancellationToken = default);

    Task ListenUdpBroadcastsAsync(
        int[] ports,
        Func<byte[], IPEndPoint, CancellationToken, Task> onDatagramReceived,
        CancellationToken cancellationToken = default);
}
public class ComunicationService : IComunicationService
{
    private const byte VT = 0x0B;   // MLLP start block
    private const byte FS = 0x1C;   // MLLP end block
    private const byte CR = 0x0D;   // MLLP end of frame

    private readonly ILogger _logger;

    /// <param name="logger">Logger of the owning connection; omit to log nothing.</param>
    public ComunicationService(ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>A frame longer than this is garbage; the buffer is dropped.</summary>
    public int MaxFrameSize { get; init; } = 1024 * 1024;

    /// <summary>
    /// A connection that has not delivered a single byte for this long is
    /// treated as broken. The monitor echoes once a second, so silence this
    /// long means the link is dead even when TCP has not noticed yet.
    /// </summary>
    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromSeconds(30);

    private readonly MemoryStream _buffer = new();

    /// <summary>Bytes waiting for the rest of their frame.</summary>
    public int Pending => (int)_buffer.Length;

    /// <summary>Append a chunk read from the socket; returns the payload of every frame it completed.</summary>
    public List<byte[]> Feed(ReadOnlySpan<byte> data)
    {
        _buffer.Write(data);
        var frames = new List<byte[]>();
        var buf = _buffer.GetBuffer().AsSpan(0, (int)_buffer.Length);

        int consumed = 0;
        while (true)
        {
            int start = buf[consumed..].IndexOf(VT);
            if (start < 0)
            {
                consumed = buf.Length;          // no frame start at all: everything is noise
                break;
            }
            start += consumed;
            int end = buf[(start + 1)..].IndexOf(FS);
            if (end < 0)
            {
                consumed = start;               // frame started but not finished yet
                break;
            }
            end += start + 1;
            frames.Add(buf[(start + 1)..end].ToArray());
            consumed = end + 1;
        }

        int remaining = buf.Length - consumed;
        if (remaining > MaxFrameSize)
        {
            remaining = 0;                      // never-ending frame: start over
        }
        if (consumed > 0 || remaining == 0)
        {
            var tail = buf[(buf.Length - remaining)..].ToArray();
            _buffer.SetLength(0);
            _buffer.Write(tail);
        }
        return frames;
    }

    /// <summary>Drop buffered bytes; call when the connection is re-established.</summary>
    public void Reset() => _buffer.SetLength(0);

    /// <summary>Wraps one message for the wire: &lt;VT&gt; payload &lt;FS&gt;&lt;CR&gt;.</summary>
    public static byte[] Frame(ReadOnlySpan<byte> payload)
    {
        var frame = new byte[payload.Length + 3];
        frame[0] = VT;
        payload.CopyTo(frame.AsSpan(1));
        frame[^2] = FS;
        frame[^1] = CR;
        return frame;
    }

    /// <summary>
    /// Connects to the monitor and pumps the stream into the MLLP frame parser
    /// until the monitor closes the link, reading fails, or the caller cancels.
    /// <paramref name="onConnected"/> runs alongside the read loop with the
    /// write side of the same socket; it is cancelled as soon as reading stops,
    /// and a failure in it ends the session just like a read failure does.
    /// </summary>
    public async Task ReadTcpStreamAsync(
        string ip,
        int port,
        Func<byte[], CancellationToken, Task> onMessageReceived,
        Func<IMonitorLink, CancellationToken, Task>? onConnected = null,
        CancellationToken cancellationToken = default)
    {
        Reset();
        using var client = new TcpClient();
        await client.ConnectAsync(ip, port, cancellationToken);

        // Send the echo without waiting for more data to batch, and let TCP
        // keep-alive notice a cable pull the monitor never told us about.
        client.NoDelay = true;
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        _logger.LogInformation("Connected to monitor {Ip}:{Port}.", ip, port);

        using var stream = client.GetStream();
        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var reader = ReadFramesAsync(stream, ip, port, onMessageReceived, sessionCts.Token);
        var writer = onConnected is null
            ? Task.CompletedTask
            : onConnected(new MonitorLink(stream), sessionCts.Token);

        await Task.WhenAny(reader, writer);
        sessionCts.Cancel();                    // whichever side is still running is done too

        // Await both sides so nothing is left unobserved. The first real
        // failure is reported; the cancellation just caused above is not one.
        foreach (var side in new[] { reader, writer })
        {
            try
            {
                await side;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
        }
    }

    private async Task ReadFramesAsync(
        NetworkStream stream,
        string ip,
        int port,
        Func<byte[], CancellationToken, Task> onMessageReceived,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[16384];

        while (!cancellationToken.IsCancellationRequested)
        {
            int read;
            using (var readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                readCts.CancelAfter(ReadTimeout);
                try
                {
                    read = await stream.ReadAsync(buffer, readCts.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        $"Monitor {ip}:{port} sent nothing for {ReadTimeout.TotalSeconds:0}s.");
                }
            }

            if (read == 0)
            {
                _logger.LogWarning("Monitor {Ip}:{Port} closed the connection.", ip, port);
                return;                         // EOF
            }

            foreach (var frame in Feed(buffer.AsSpan(0, read)))
            {
                await onMessageReceived(frame, cancellationToken);
            }
        }
    }

    /// <summary>Binds to UDP ports to receive ADT broadcast datagrams.</summary>
    public async Task ListenUdpBroadcastsAsync(
        int[] ports,
        Func<byte[], IPEndPoint, CancellationToken, Task> onDatagramReceived,
        CancellationToken cancellationToken = default)
    {
        var tasks = ports.Select(port => Task.Run(async () =>
        {
            using var udpClient = new UdpClient();
            udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, port));

            while (!cancellationToken.IsCancellationRequested)
            {
                UdpReceiveResult result;
                try
                {
                    result = await udpClient.ReceiveAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (SocketException ex)
                {
                    // Network socket error, retry listening
                    _logger.LogWarning(ex, "UDP {Port}: receive failed, listening again.", port);
                    continue;
                }

                try
                {
                    await onDatagramReceived(result.Buffer, result.RemoteEndPoint, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // One bad datagram must not stop the listener.
                    _logger.LogError(ex, "UDP {Port}: dropped a datagram from {Remote}.",
                        port, result.RemoteEndPoint);
                }
            }
        }, cancellationToken)).ToArray();

        await Task.WhenAll(tasks);
    }

    /// <summary>Write side of one connection; sends are framed and serialized.</summary>
    private sealed class MonitorLink : IMonitorLink
    {
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly Stream _stream;

        public MonitorLink(Stream stream) => _stream = stream;

        public async Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        {
            var frame = Frame(payload.Span);
            await _sendLock.WaitAsync(cancellationToken);
            try
            {
                await _stream.WriteAsync(frame, cancellationToken);
                await _stream.FlushAsync(cancellationToken);
            }
            finally
            {
                _sendLock.Release();
            }
        }
    }
}
