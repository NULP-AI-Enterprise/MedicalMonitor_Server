using System.Net;
using System.Net.Sockets;

namespace MedicalMonitorServer.DeviceDriver;

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

    /// <summary>A frame longer than this is garbage; the buffer is dropped.</summary>
    public int MaxFrameSize { get; init; } = 1024 * 1024;

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

    /// <summary>Connects to TCP socket and pumps stream into MLLP frame parser.</summary>
    public async Task ReadTcpStreamAsync(
        string ip,
        int port,
        Func<byte[], CancellationToken, Task> onMessageReceived,
        CancellationToken cancellationToken = default)
    {
        Reset();
        using var client = new TcpClient();
        await client.ConnectAsync(ip, port, cancellationToken);
        using var stream = client.GetStream();
        var buffer = new byte[16384];

        while (!cancellationToken.IsCancellationRequested)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0) break; // EOF

            var frames = Feed(buffer.AsSpan(0, read));
            foreach (var frame in frames)
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
                try
                {
                    var result = await udpClient.ReceiveAsync(cancellationToken);
                    await onDatagramReceived(result.Buffer, result.RemoteEndPoint, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (SocketException)
                {
                    // Network socket error, retry listening
                }
            }
        }, cancellationToken)).ToArray();

        await Task.WhenAll(tasks);
    }
}

