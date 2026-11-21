using System.Net;
using System.Net.Sockets;
using System.Text;
using MedicalMonitorServer.DeviceDriver;
using MedicalMonitorServer.DeviceDriver.Umec;

namespace MedicalMonitorServer.DeviceDriver.Tests;

/// <summary>
/// The regression this change exists for, end to end: the real
/// <see cref="WatchDogService"/> wired to the real <see cref="DeviceService"/>
/// and the real driver, exactly as Program.cs does it.
/// </summary>
/// <remarks>
/// A monitor serves a limited number of HL7 clients on port 4601. Once the
/// driver holds a link, a probe to that port can be refused — slot exhausted,
/// a dropped SYN, or the 500 ms probe deadline missed under load. Before this
/// change that refusal was reported as <c>Disconnected</c>, which made
/// <see cref="DeviceService"/> call <c>Disconnect</c> and dispose a perfectly
/// healthy stream. Here the listener stops accepting once the link is up, so
/// every later probe would be refused.
/// </remarks>
public class WatchDogLinkTeardownTests : IDisposable
{
    private readonly CancellationTokenSource _cts = new();

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }

    private static byte[] EchoFrame() => ComunicationService.Frame(
        Encoding.Latin1.GetBytes("MSH|^~\\&|||||||ORU^R01|106|P|2.3.1|\r"));

    [Fact]
    public async Task The_watchdog_does_not_tear_down_a_link_the_driver_owns()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        int accepted = 0;
        var linkUp = new TaskCompletionSource();
        bool driverLinkClosed = false;
        int echoesSeen = 0;

        // The monitor: accepts clients until the driver's link arrives, then stops
        // listening and serves only that one stream. A probe connects and says
        // nothing; the driver sends its start query straight away.
        var monitor = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(_cts.Token);
                }
                catch (Exception)
                {
                    return;
                }

                Interlocked.Increment(ref accepted);
                var stream = client.GetStream();
                var buffer = new byte[4096];

                int read;
                try
                {
                    using var firstRead = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                    firstRead.CancelAfter(TimeSpan.FromMilliseconds(400));
                    read = await stream.ReadAsync(buffer, firstRead.Token);
                }
                catch (Exception)
                {
                    client.Dispose();
                    continue;                       // a probe: connected, said nothing
                }

                if (read == 0)
                {
                    client.Dispose();
                    continue;                       // a probe that closed at once
                }

                // This is the driver. From here on nobody else gets in.
                listener.Stop();
                linkUp.TrySetResult();

                try
                {
                    while (!_cts.IsCancellationRequested)
                    {
                        await stream.WriteAsync(EchoFrame(), _cts.Token);
                        int more = await stream.ReadAsync(buffer, _cts.Token);
                        if (more == 0)
                        {
                            driverLinkClosed = true; // the server dropped our link
                            return;
                        }

                        Interlocked.Increment(ref echoesSeen);
                        await Task.Delay(20, _cts.Token);
                    }
                }
                catch (Exception) when (!_cts.IsCancellationRequested)
                {
                    driverLinkClosed = true;
                    return;
                }

                return;
            }
        });

        // Wired exactly as Program.cs:56-58 plus WatchDogHostedService: the
        // DeviceService is constructed first, which is what installs both the
        // transition handler and the ownership check.
        var watchDogOptions = new WatchDogOptions
        {
            IpBase = "127.0.0.",
            StartIp = 1,
            EndIp = 1,
            Port = port,
            ConnectTimeoutMs = 2000,
            ScanIntervalMs = 20,
        };
        var watchDog = new WatchDogService(watchDogOptions);
        var deviceService = new DeviceService(
            watchDog,
            loggerFactory: null,
            linkOptions: new UmecLinkOptions
            {
                ReconnectDelay = TimeSpan.FromMilliseconds(30),
                EchoInterval = TimeSpan.FromMilliseconds(20),
                QueryRefreshInterval = TimeSpan.FromMilliseconds(50),
                ReadTimeout = TimeSpan.FromSeconds(2),
                ConnectTimeout = TimeSpan.FromMilliseconds(200),
                MaxFailedSessions = 5,
            });

        var scan = Task.Run(() => watchDog.StartWatchDog(_cts.Token));

        await linkUp.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Wait.UntilAsync(() => deviceService.GetAllDevice().Count == 1,
            "the driver reports the monitor as connected");

        int acceptsWhenLinkCameUp = Volatile.Read(ref accepted);

        // Many scan intervals during which every probe would be refused.
        await Task.Delay(500);

        Assert.False(driverLinkClosed,
            "the watchdog tore down a healthy HL7 link after a refused probe");
        Assert.Single(deviceService.GetAllDevice());
        Assert.True(Volatile.Read(ref echoesSeen) > 3,
            "the link must still be carrying traffic");
        Assert.Equal(acceptsWhenLinkCameUp, Volatile.Read(ref accepted));

        Assert.False(scan.IsFaulted, "the scan loop must still be running");
        _cts.Cancel();
    }

    [Fact]
    public async Task A_monitor_that_was_never_reached_is_still_probed_repeatedly()
    {
        // The counterpart: with nothing owned, the watchdog must keep probing, or
        // a monitor coming online would never be discovered.
        var spare = new TcpListener(IPAddress.Loopback, 0);
        spare.Start();
        int deadPort = ((IPEndPoint)spare.LocalEndpoint).Port;
        spare.Stop();

        var watchDog = new WatchDogService(new WatchDogOptions
        {
            IpBase = "127.0.0.",
            StartIp = 1,
            EndIp = 1,
            Port = deadPort,
            ConnectTimeoutMs = 2000,
            ScanIntervalMs = 20,
        });
        _ = new DeviceService(watchDog, loggerFactory: null);

        int transitions = 0;
        var previous = watchDog.OnDeviceStateChanged;
        watchDog.OnDeviceStateChanged = state =>
        {
            Interlocked.Increment(ref transitions);
            previous?.Invoke(state);
        };

        _ = Task.Run(() => watchDog.StartWatchDog(_cts.Token));

        await Wait.UntilAsync(() => Volatile.Read(ref transitions) >= 1, "the empty address is reported");
        await Task.Delay(200);

        // Edge-triggered, so exactly one transition — but the address is in the map,
        // which only happens if it was really probed.
        Assert.Equal(1, Volatile.Read(ref transitions));
        Assert.True(watchDog.device_map.ContainsKey("127.0.0.1"));
    }
}
