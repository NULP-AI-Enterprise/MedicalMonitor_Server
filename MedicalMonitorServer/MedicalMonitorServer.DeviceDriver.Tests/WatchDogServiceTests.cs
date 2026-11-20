using System.Net;
using System.Net.Sockets;
using MedicalMonitorServer.Contracts.Models;
using MedicalMonitorServer.DeviceDriver;

namespace MedicalMonitorServer.DeviceDriver.Tests;

/// <summary>
/// The watchdog's scan, against listeners on the loopback interface. The point
/// of these is the probe suppression: a monitor serves a limited number of HL7
/// clients on the port being probed, so an address whose link the driver
/// already holds must be left alone — and a probe that fails there would
/// report the monitor as gone and tear the working link down.
/// </summary>
public class WatchDogServiceTests : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly List<TcpListener> _listeners = new();

    public void Dispose()
    {
        _cts.Cancel();
        foreach (var listener in _listeners)
        {
            try { listener.Stop(); } catch (SocketException) { }
        }

        _cts.Dispose();
    }

    /// <summary>A listener on 127.0.0.x that counts how many clients it accepted.</summary>
    private (TcpListener Listener, int Port, Func<int> Accepted) Listen(string host = "127.0.0.1")
    {
        var listener = new TcpListener(IPAddress.Parse(host), 0);
        listener.Start();
        _listeners.Add(listener);
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        int accepted = 0;

        _ = Task.Run(async () =>
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
                    return;             // stopped or cancelled
                }

                Interlocked.Increment(ref accepted);
                client.Dispose();       // back to accepting at once, so no backlog builds
            }
        });

        return (listener, port, () => Volatile.Read(ref accepted));
    }

    /// <summary>A port nothing listens on, so every connect is refused.</summary>
    private static int DeadPort()
    {
        var spare = new TcpListener(IPAddress.Loopback, 0);
        spare.Start();
        int port = ((IPEndPoint)spare.LocalEndpoint).Port;
        spare.Stop();
        return port;
    }

    private static WatchDogOptions Options(int port, int lastOctet = 1) => new()
    {
        IpBase = "127.0.0.",
        StartIp = 1,
        EndIp = lastOctet,
        Port = port,
        ConnectTimeoutMs = 2000,
        ScanIntervalMs = 20,
    };

    /// <summary>
    /// One reported transition, captured by value. The watchdog reuses and mutates
    /// the same <see cref="DeviceState"/> instance for an address, so a test that
    /// kept the object would read every transition as the latest state.
    /// </summary>
    private readonly record struct Seen(string Ip, DeviceStateEnum State);

    private static Action<DeviceState> Collect(List<Seen> into) => state =>
    {
        lock (into) { into.Add(new Seen(state.IpAddress, state.State)); }
    };

    /// <summary>Runs the scan in the background and collects every transition it reports.</summary>
    private (WatchDogService Service, List<Seen> Transitions) Start(WatchDogOptions options)
    {
        var transitions = new List<Seen>();
        var service = new WatchDogService(options) { OnDeviceStateChanged = Collect(transitions) };

        _ = Task.Run(() => service.StartWatchDog(_cts.Token));
        return (service, transitions);
    }

    private static List<DeviceStateEnum> Snapshot(List<Seen> transitions)
    {
        lock (transitions) { return transitions.Select(t => t.State).ToList(); }
    }

    // ------------------------------------------------------ baseline behaviour

    [Fact]
    public async Task An_address_with_a_listener_is_reported_connected_once()
    {
        var (_, port, _) = Listen();
        var (_, transitions) = Start(Options(port));

        await Wait.UntilAsync(() => Snapshot(transitions).Count >= 1, "the monitor is found");
        await Task.Delay(120);              // several more scans

        Assert.Equal(new[] { DeviceStateEnum.Connected }, Snapshot(transitions));
    }

    [Fact]
    public async Task An_address_nothing_listens_on_is_reported_disconnected_once()
    {
        var (_, transitions) = Start(Options(DeadPort()));

        await Wait.UntilAsync(() => Snapshot(transitions).Count >= 1, "the empty address is reported");
        await Task.Delay(120);

        Assert.Equal(new[] { DeviceStateEnum.Disconnected }, Snapshot(transitions));
    }

    [Fact]
    public async Task A_listener_that_goes_away_is_reported_disconnected()
    {
        var (listener, port, _) = Listen();
        var (_, transitions) = Start(Options(port));

        await Wait.UntilAsync(() => Snapshot(transitions).Count == 1, "the monitor is found");
        listener.Stop();

        await Wait.UntilAsync(() => Snapshot(transitions).Count == 2, "its disappearance is reported");
        Assert.Equal(
            new[] { DeviceStateEnum.Connected, DeviceStateEnum.Disconnected },
            Snapshot(transitions));
    }

    [Fact]
    public async Task With_no_ownership_check_every_address_is_probed()
    {
        var (_, port, accepted) = Listen();
        var (service, _) = Start(Options(port));

        Assert.Null(service.IsLinkOwned);                       // unchanged default
        await Wait.UntilAsync(() => accepted() >= 3, "the probe keeps running");
    }

    // ------------------------------------------------------ probe suppression

    [Fact]
    public async Task An_address_whose_link_is_already_owned_is_never_probed()
    {
        var (_, port, accepted) = Listen();
        var options = Options(port);
        var transitions = new List<Seen>();
        var service = new WatchDogService(options)
        {
            IsLinkOwned = _ => true,
            OnDeviceStateChanged = Collect(transitions),
        };

        _ = Task.Run(() => service.StartWatchDog(_cts.Token));
        await Task.Delay(250);              // ~12 scan intervals

        Assert.Equal(0, accepted());                            // no slot taken
        Assert.Empty(Snapshot(transitions));
        Assert.Empty(service.device_map);                       // and nothing invented
    }

    [Fact]
    public async Task An_owned_address_keeps_the_state_the_watchdog_last_saw()
    {
        // The regression this whole change is about: the link is owned and a
        // probe would now fail, which previously reported the monitor as gone.
        var (listener, port, _) = Listen();
        bool owned = false;
        var options = Options(port);
        var transitions = new List<Seen>();
        var service = new WatchDogService(options)
        {
            IsLinkOwned = _ => Volatile.Read(ref owned),
            OnDeviceStateChanged = Collect(transitions),
        };

        _ = Task.Run(() => service.StartWatchDog(_cts.Token));
        await Wait.UntilAsync(() => Snapshot(transitions).Count == 1, "the monitor is found");

        Volatile.Write(ref owned, true);
        listener.Stop();                    // a probe would be refused from here on
        await Task.Delay(250);

        Assert.Equal(new[] { DeviceStateEnum.Connected }, Snapshot(transitions));
        Assert.Equal(DeviceStateEnum.Connected, service.device_map["127.0.0.1"].State);
    }

    [Fact]
    public async Task A_released_address_is_probed_again()
    {
        // Suppression must not be sticky, or a dead monitor would stay "connected" forever.
        var (listener, port, _) = Listen();
        bool owned = false;
        var transitions = new List<Seen>();
        var service = new WatchDogService(Options(port))
        {
            IsLinkOwned = _ => Volatile.Read(ref owned),
            OnDeviceStateChanged = Collect(transitions),
        };

        _ = Task.Run(() => service.StartWatchDog(_cts.Token));
        await Wait.UntilAsync(() => Snapshot(transitions).Count == 1, "the monitor is found");

        Volatile.Write(ref owned, true);
        listener.Stop();
        await Task.Delay(100);
        Assert.Single(Snapshot(transitions));

        Volatile.Write(ref owned, false);   // the driver gave up on the link
        await Wait.UntilAsync(() => Snapshot(transitions).Count == 2, "probing resumes and finds it gone");
        Assert.Equal(DeviceStateEnum.Disconnected, Snapshot(transitions)[1]);
    }

    [Fact]
    public async Task An_owned_address_that_was_never_seen_is_not_added_to_the_map()
    {
        var (_, port, accepted) = Listen();
        var transitions = new List<Seen>();
        var service = new WatchDogService(Options(port))
        {
            IsLinkOwned = _ => true,
            OnDeviceStateChanged = Collect(transitions),
        };

        _ = Task.Run(() => service.StartWatchDog(_cts.Token));
        await Task.Delay(200);

        Assert.Empty(service.device_map);
        Assert.Empty(Snapshot(transitions));
        Assert.Equal(0, accepted());
    }

    [Fact]
    public async Task Other_addresses_are_still_probed_while_one_is_owned()
    {
        // 127.0.0.1 is owned; 127.0.0.2 routes to loopback too but has no listener.
        var (_, port, accepted) = Listen();
        var transitions = new List<Seen>();
        var service = new WatchDogService(Options(port, lastOctet: 2))
        {
            IsLinkOwned = ip => ip == "127.0.0.1",
            OnDeviceStateChanged = Collect(transitions),
        };

        _ = Task.Run(() => service.StartWatchDog(_cts.Token));
        await Wait.UntilAsync(() => Snapshot(transitions).Count >= 1, "the other address is reported");

        lock (transitions)
        {
            Assert.All(transitions, t => Assert.Equal("127.0.0.2", t.Ip));
        }

        Assert.Equal(0, accepted());        // the owned one was left alone
    }

    [Fact]
    public async Task An_ownership_check_that_throws_does_not_stop_the_scan()
    {
        // StartWatchDog's task goes straight back to the host, and .NET 8 stops
        // the host on an unhandled exception from a BackgroundService.
        var (_, port, _) = Listen();
        var transitions = new List<Seen>();
        var service = new WatchDogService(Options(port))
        {
            IsLinkOwned = _ => throw new InvalidOperationException("ownership check is broken"),
            OnDeviceStateChanged = Collect(transitions),
        };

        var run = Task.Run(() => service.StartWatchDog(_cts.Token));

        // Falls back to probing, so the monitor is still found, and the loop lives.
        await Wait.UntilAsync(() => Snapshot(transitions).Count >= 1, "the scan carries on");
        Assert.Equal(DeviceStateEnum.Connected, Snapshot(transitions)[0]);
        Assert.False(run.IsFaulted, "the scan task must not fault");
    }
}
