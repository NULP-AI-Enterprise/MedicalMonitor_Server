using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace MedicalMonitorServer.DeviceDriver.NihonKohden.LsNet;

/// <param name="LocalPort">Port it arrived on: 8300 control, 8301 streams.</param>
public readonly record struct LsNetReceived(IPAddress From, int LocalPort, byte[] Data);

/// <summary>The UDP side of LS-NET, behind an interface so the provider can be driven without a monitor.</summary>
public interface ILsNetTransport : IDisposable
{
    /// <summary>Every datagram that reaches the control or the stream port.</summary>
    ChannelReader<LsNetReceived> Incoming { get; }

    /// <summary>Sends a control datagram to one node's control port.</summary>
    void Send(byte[] datagram, IPAddress to);

    /// <summary>Sends a datagram to every node on the monitor network.</summary>
    void Broadcast(byte[] datagram);
}

/// <summary>
/// Three sockets: control datagrams leave from port 55405 of the address this PC has on the
/// monitor network, and answers and streams come in on ports 8300 and 8301 of any address.
/// </summary>
internal sealed class LsNetUdpTransport : ILsNetTransport
{
    private readonly Channel<LsNetReceived> _incoming = Channel.CreateBounded<LsNetReceived>(
        new BoundedChannelOptions(4096) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly CancellationTokenSource _stopping = new();
    private readonly UdpClient _sender;
    private readonly List<UdpClient> _listeners = new();
    private readonly IPEndPoint[] _broadcastTargets;
    private readonly ILogger _logger;

    public LsNetUdpTransport(IPAddress localAddress, ILogger logger)
    {
        _logger = logger;

        _sender = new UdpClient(new IPEndPoint(localAddress, LsNetProtocol.ControlSourcePort)) { EnableBroadcast = true };

        // The BSM-3562 was reached with both forms of broadcast going out; which one it needs
        // was not separated, so both are sent.
        var targets = new List<IPEndPoint> { new(IPAddress.Broadcast, LsNetProtocol.ControlPort) };
        if (DirectedBroadcast(localAddress) is { } directed) targets.Add(new IPEndPoint(directed, LsNetProtocol.ControlPort));
        _broadcastTargets = targets.ToArray();

        foreach (int port in new[] { LsNetProtocol.ControlPort, LsNetProtocol.StreamPort })
        {
            var listener = new UdpClient(new IPEndPoint(IPAddress.Any, port));
            _listeners.Add(listener);
            _ = ListenAsync(listener, port, localAddress);
        }
    }

    public ChannelReader<LsNetReceived> Incoming => _incoming.Reader;

    public void Send(byte[] datagram, IPAddress to) => SendTo(datagram, new IPEndPoint(to, LsNetProtocol.ControlPort));

    public void Broadcast(byte[] datagram)
    {
        foreach (var target in _broadcastTargets) SendTo(datagram, target);
    }

    private void SendTo(byte[] datagram, IPEndPoint to)
    {
        try
        {
            _sender.Send(datagram, datagram.Length, to);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            // The cable is out or we are shutting down; the next beacon tries again.
            _logger.LogDebug("LS-NET: sending to {Target} failed: {Reason}", to, ex.Message);
        }
    }

    private async Task ListenAsync(UdpClient listener, int port, IPAddress localAddress)
    {
        while (!_stopping.IsCancellationRequested)
        {
            try
            {
                var result = await listener.ReceiveAsync(_stopping.Token);

                // Our own broadcasts come back to the control port.
                if (!result.RemoteEndPoint.Address.Equals(localAddress))
                {
                    _incoming.Writer.TryWrite(new LsNetReceived(result.RemoteEndPoint.Address, port, result.Buffer));
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex)
            {
                // Windows reports an ICMP "port unreachable" for something we sent as a receive error.
                _logger.LogDebug("LS-NET: receive on port {Port} failed: {Reason}", port, ex.Message);
            }
        }
    }

    /// <summary>The broadcast address of the subnet <paramref name="localAddress"/> is in; null when it is not on this machine.</summary>
    private static IPAddress? DirectedBroadcast(IPAddress localAddress)
    {
        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            foreach (var unicast in networkInterface.GetIPProperties().UnicastAddresses)
            {
                if (!unicast.Address.Equals(localAddress) || unicast.IPv4Mask is null) continue;

                var address = localAddress.GetAddressBytes();
                var mask = unicast.IPv4Mask.GetAddressBytes();
                for (int i = 0; i < address.Length; i++) address[i] |= (byte)~mask[i];
                return new IPAddress(address);
            }
        }

        return null;
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _sender.Dispose();
        foreach (var listener in _listeners) listener.Dispose();
        _incoming.Writer.TryComplete();
    }
}
