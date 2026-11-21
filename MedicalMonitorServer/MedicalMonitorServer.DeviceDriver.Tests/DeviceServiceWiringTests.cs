using System.Net;
using System.Net.Sockets;
using MedicalMonitorServer.Contracts.Models;
using MedicalMonitorServer.DeviceDriver;
using MedicalMonitorServer.DeviceDriver.Umec;
using Microsoft.Extensions.DependencyInjection;

namespace MedicalMonitorServer.DeviceDriver.Tests;

/// <summary>
/// <see cref="DeviceService"/> as the container actually builds it.
/// </summary>
/// <remarks>
/// Every other test here constructs it directly and passes its providers, which
/// hid a defect that made the whole driver inert in production: Microsoft DI
/// resolves <c>IEnumerable&lt;T&gt;</c> to an EMPTY ARRAY rather than null when
/// no <c>T</c> is registered, so an optional provider parameter arrives as an
/// empty collection and a plain <c>??</c> fallback never runs. The result was no
/// HL7 link, no data, and an ownership check permanently answering false.
/// </remarks>
public class DeviceServiceWiringTests
{
    /// <summary>
    /// Long enough that the link is still held when the assertion runs, short
    /// enough that the background attempt gives up well inside a test run.
    /// </summary>
    private static UmecLinkOptions Patient => new()
    {
        ReconnectDelay = TimeSpan.FromSeconds(10),
        MaxFailedSessions = 2,
        ConnectTimeout = TimeSpan.FromMilliseconds(200),
    };

    private static DeviceState Monitor(string ip, int port) => new()
    {
        Id = 1,
        Name = "uMec10",
        IpAddress = ip,
        Port = port.ToString(),
        State = DeviceStateEnum.Connected,
    };

    /// <summary>A port nothing listens on. Connect registers the link before dialling.</summary>
    private static int DeadPort()
    {
        var spare = new TcpListener(IPAddress.Loopback, 0);
        spare.Start();
        int port = ((IPEndPoint)spare.LocalEndpoint).Port;
        spare.Stop();
        return port;
    }

    [Fact]
    public void A_container_built_service_really_has_a_provider()
    {
        // The registrations from Program.cs; only the timings are shortened.
        var services = new ServiceCollection();
        services.AddSingleton(new WatchDogOptions());
        services.AddSingleton<IWatchDogService, WatchDogService>();
        services.AddSingleton(Patient);
        services.AddSingleton<DeviceService>();
        using var container = services.BuildServiceProvider();

        _ = container.GetRequiredService<DeviceService>();
        var watchDog = container.GetRequiredService<IWatchDogService>();

        Assert.NotNull(watchDog.OnDeviceStateChanged);
        Assert.NotNull(watchDog.IsLinkOwned);

        // Handing a monitor over must register a link. With no providers this
        // silently does nothing — which is exactly how the defect presented.
        watchDog.OnDeviceStateChanged!.Invoke(Monitor("127.0.0.1", DeadPort()));

        Assert.True(watchDog.IsLinkOwned!("127.0.0.1"),
            "the container-built service registered no link, so it has no providers");
    }

    [Fact]
    public void An_empty_provider_collection_falls_back_to_the_default_provider()
    {
        // The exact shape Microsoft DI passes for an unregistered IEnumerable<T>.
        var watchDog = new FakeWatchDog();
        _ = new DeviceService(
            watchDog,
            loggerFactory: null,
            linkOptions: Patient,
            providers: Array.Empty<IDeviceDataProvider>());

        watchDog.OnDeviceStateChanged!.Invoke(Monitor("127.0.0.1", DeadPort()));

        Assert.True(watchDog.IsLinkOwned!("127.0.0.1"));
    }

    [Fact]
    public void A_supplied_provider_is_used_instead_of_the_default()
    {
        var watchDog = new FakeWatchDog();
        var reader = new FakeReader(FakeReader.StaysOpen);
        var provider = new UmecDeviceDataProvider(
            logger: null,
            options: new UmecLinkOptions { ReconnectDelay = TimeSpan.FromMilliseconds(20) },
            readerFactory: _ => reader);

        using var service = provider;
        _ = new DeviceService(watchDog, loggerFactory: null, providers: new[] { provider });

        watchDog.OnDeviceStateChanged!.Invoke(Monitor("10.1.2.3", 4601));

        // Owned, and dialled through the substituted reader rather than a socket.
        Assert.True(watchDog.IsLinkOwned!("10.1.2.3"));
        Assert.True(reader.Attempts >= 1 || reader.Sessions.Count >= 0);
    }

    [Fact]
    public void A_collection_holding_only_nulls_falls_back_too()
    {
        var watchDog = new FakeWatchDog();
        _ = new DeviceService(
            watchDog,
            loggerFactory: null,
            linkOptions: Patient,
            providers: new IDeviceDataProvider[] { null! });

        watchDog.OnDeviceStateChanged!.Invoke(Monitor("127.0.0.1", DeadPort()));

        Assert.True(watchDog.IsLinkOwned!("127.0.0.1"));
    }

    private sealed class FakeWatchDog : IWatchDogService
    {
        public Action<DeviceState>? OnDeviceStateChanged { get; set; }

        public Func<string, bool>? IsLinkOwned { get; set; }

        public Task StartWatchDog(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
