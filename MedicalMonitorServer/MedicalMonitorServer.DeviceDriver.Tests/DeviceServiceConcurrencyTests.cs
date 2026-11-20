using System.Collections.Concurrent;
using MedicalMonitorServer.Contracts.Models;
using MedicalMonitorServer.DeviceDriver;
using MedicalMonitorServer.DeviceDriver.Umec;

namespace MedicalMonitorServer.DeviceDriver.Tests;

/// <summary>
/// <see cref="DeviceService"/> keeps its list of connected devices in a plain
/// dictionary of lists, and three kinds of thread reach it at once: the
/// watchdog's scan (a monitor went away), the link threads (a stream opened or
/// ended) and HTTP requests (<c>/devices</c>, <c>/health</c>). These tests run
/// all three against each other.
/// </summary>
public class DeviceServiceConcurrencyTests
{
    private sealed class FakeWatchDog : IWatchDogService
    {
        public Action<DeviceState>? OnDeviceStateChanged { get; set; }

        public Func<string, bool>? IsLinkOwned { get; set; }

        public Task StartWatchDog(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    /// <summary>A provider whose link events the test raises by hand.</summary>
    private sealed class FakeProvider : IDeviceDataProvider
    {
        public event Action<IDeviceDataProvider, DeviceState>? OnDeviceConnected;

        public event Action<IDeviceDataProvider, DeviceState>? OnDeviceDisconnected;

#pragma warning disable CS0067 // never raised: these tests are about connection state only
        public event Action<UmecMessage>? OnMessageReceived;
#pragma warning restore CS0067

        public int Disconnects;

        /// <summary>Runs inside <see cref="Disconnect"/>, to stand in for what closing a real link does.</summary>
        public Action<DeviceState>? WhileDisconnecting;

        public IReadOnlyCollection<DeviceState> ConnectedDevices => Array.Empty<DeviceState>();

        public IReadOnlyCollection<PatientInfo> Patients => Array.Empty<PatientInfo>();

        public void Connect(DeviceState deviceState) { }

        public void Disconnect(DeviceState deviceState)
        {
            Interlocked.Increment(ref Disconnects);
            WhileDisconnecting?.Invoke(deviceState);
        }

        public void LinkUp(DeviceState state) => OnDeviceConnected?.Invoke(this, state);

        public void LinkDown(DeviceState state) => OnDeviceDisconnected?.Invoke(this, state);

        public void Dispose() { }
    }

    private static DeviceState Device(string ip, DeviceStateEnum state = DeviceStateEnum.Connected) => new()
    {
        Id = 1,
        Name = "uMec10",
        IpAddress = ip,
        Port = "4601",
        State = state,
    };

    [Fact]
    public async Task Link_events_watchdog_events_and_readers_can_run_at_once()
    {
        // As in production: ONE provider (uMec) and one link thread per monitor,
        // so every link thread mutates the same list.
        const int linkThreads = 8;
        const int devicesPerThread = 16;

        var watchDog = new FakeWatchDog();
        var provider = new FakeProvider();
        var service = new DeviceService(watchDog, loggerFactory: null, providers: new[] { provider });

        var failures = new ConcurrentQueue<Exception>();
        using var stop = new CancellationTokenSource();
        var allRunning = new TaskCompletionSource();
        using var start = new Barrier(linkThreads + 3, _ => allRunning.TrySetResult());
        long iterations = 0;

        void Guarded(Action body)
        {
            start.SignalAndWait();
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    body();
                    Interlocked.Increment(ref iterations);
                }
                catch (Exception ex)
                {
                    failures.Enqueue(ex);
                    return;
                }
            }
        }

        // Dedicated threads: pool threads blocked on the barrier are injected so
        // slowly that the run could be over before the loops had even started.
        Task Spawn(Action body) =>
            Task.Factory.StartNew(() => Guarded(body), TaskCreationOptions.LongRunning);

        var threads = new List<Task>();

        // Link threads: streams opening and ending, each for its own monitors.
        for (int t = 0; t < linkThreads; t++)
        {
            int index = t;
            var devices = Enumerable.Range(0, devicesPerThread)
                .Select(d => Device($"10.{index}.0.{d}"))
                .ToArray();

            threads.Add(Spawn(() =>
            {
                foreach (var device in devices) provider.LinkUp(device);
                foreach (var device in devices) provider.LinkDown(device);
            }));
        }

        // HTTP threads: /devices and /health enumerate the whole list.
        for (int r = 0; r < 2; r++)
        {
            threads.Add(Spawn(() =>
            {
                foreach (var device in service.GetAllDevice())
                {
                    _ = device.IpAddress.Length;
                }
            }));
        }

        // Watchdog thread: monitors reported as gone.
        threads.Add(Spawn(() =>
        {
            for (int t = 0; t < linkThreads; t++)
            {
                watchDog.OnDeviceStateChanged!.Invoke(
                    Device($"10.{t}.0.3", DeviceStateEnum.Disconnected));
            }
        }));

        await allRunning.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(500);
        stop.Cancel();
        await Task.WhenAll(threads).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(failures.IsEmpty,
            $"concurrent access corrupted the device list: {failures.FirstOrDefault()}");

        // A run in which the loops never turned would prove nothing.
        Assert.True(Interlocked.Read(ref iterations) > 100,
            $"the threads barely ran ({iterations} iterations), so nothing was exercised");

        // Every link that came up also went down, so nothing may be left behind.
        Assert.Empty(service.GetAllDevice());
    }

    [Fact]
    public void A_snapshot_is_returned_so_a_caller_can_enumerate_while_links_change()
    {
        var watchDog = new FakeWatchDog();
        var provider = new FakeProvider();
        var service = new DeviceService(watchDog, loggerFactory: null, providers: new[] { provider });

        provider.LinkUp(Device("10.0.0.1"));
        provider.LinkUp(Device("10.0.0.2"));

        var snapshot = service.GetAllDevice();
        provider.LinkDown(Device("10.0.0.1"));          // changes the live list mid-"enumeration"

        Assert.Equal(2, snapshot.Count);
        Assert.Single(service.GetAllDevice());
    }

    [Fact]
    public async Task Closing_a_link_does_not_hold_the_device_list()
    {
        // Disconnect cancels a link's run loop and can take a while. Were it called
        // with the list locked, /devices and /health would stall behind it, and so
        // would every other link reporting its own state.
        var watchDog = new FakeWatchDog();
        var provider = new FakeProvider();
        var service = new DeviceService(watchDog, loggerFactory: null, providers: new[] { provider });
        using var release = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        provider.WhileDisconnecting = _ =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
        };

        provider.LinkUp(Device("10.0.0.1"));
        var closing = Task.Factory.StartNew(
            () => watchDog.OnDeviceStateChanged!.Invoke(Device("10.0.0.1", DeviceStateEnum.Disconnected)),
            TaskCreationOptions.LongRunning);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)), "Disconnect was never reached");

        // While that Disconnect is still in progress:
        var reading = Task.Run(() => service.GetAllDevice());
        var linking = Task.Run(() => provider.LinkUp(Device("10.0.0.2")));
        await Task.WhenAll(reading, linking).WaitAsync(TimeSpan.FromSeconds(2));

        release.Set();
        await closing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("10.0.0.2", Assert.Single(service.GetAllDevice()).IpAddress);
    }

    [Fact]
    public void Closing_a_link_may_report_its_own_end_before_it_returns()
    {
        // Cancelling a run loop can execute its continuation inline, so the link's
        // "stream ended" report arrives on the very thread that is disconnecting it.
        var watchDog = new FakeWatchDog();
        var provider = new FakeProvider();
        var service = new DeviceService(watchDog, loggerFactory: null, providers: new[] { provider });
        provider.WhileDisconnecting = state => provider.LinkDown(state);

        provider.LinkUp(Device("10.0.0.1"));
        watchDog.OnDeviceStateChanged!.Invoke(Device("10.0.0.1", DeviceStateEnum.Disconnected));

        Assert.Equal(1, provider.Disconnects);
        Assert.Empty(service.GetAllDevice());
    }

    [Fact]
    public void A_monitor_reported_gone_is_disconnected_exactly_once_and_dropped()
    {
        var watchDog = new FakeWatchDog();
        var provider = new FakeProvider();
        var service = new DeviceService(watchDog, loggerFactory: null, providers: new[] { provider });

        provider.LinkUp(Device("10.0.0.1"));
        watchDog.OnDeviceStateChanged!.Invoke(Device("10.0.0.1", DeviceStateEnum.Disconnected));

        Assert.Equal(1, provider.Disconnects);
        Assert.Empty(service.GetAllDevice());

        // A second report for a device no longer listed does nothing.
        watchDog.OnDeviceStateChanged!.Invoke(Device("10.0.0.1", DeviceStateEnum.Disconnected));
        Assert.Equal(1, provider.Disconnects);
    }
}
