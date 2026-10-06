using System.Net.Sockets;

namespace MedicalMonitorServer.DeviceDriver;

public sealed class ComunicationService : IAsyncDisposable
{
    private readonly TcpClient _client = new();
    private NetworkStream? _stream;

    public event Func<ReadOnlyMemory<byte>, Task>? DataReceived;

    public async Task ConnectAsync(string ipAddress, int port, CancellationToken cancellationToken)
    {
        await _client.ConnectAsync(ipAddress, port, cancellationToken);
        _stream = _client.GetStream();

        var buffer = new byte[4096];
        while (!cancellationToken.IsCancellationRequested)
        {
            var bytesRead = await _stream.ReadAsync(buffer, cancellationToken);
            if (bytesRead == 0)
            {
                return;
            }

            if (DataReceived is not null)
            {
                await DataReceived(buffer.AsMemory(0, bytesRead));
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_stream is not null)
        {
            await _stream.DisposeAsync();
        }

        _client.Dispose();
    }
}