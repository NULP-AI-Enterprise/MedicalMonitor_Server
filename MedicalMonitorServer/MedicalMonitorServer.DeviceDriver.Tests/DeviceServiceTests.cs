using MedicalMonitorServer.Contracts.Models;
using MedicalMonitorServer.DeviceDriver;
using MedicalMonitorServer.DeviceDriver.Umec;

namespace MedicalMonitorServer.DeviceDriver.Tests;

/// <summary>
/// How <see cref="DeviceService"/> answers the watchdog's link-ownership
/// question. The window it reports must span the whole life of a link,
/// including the pause between reconnect attempts — otherwise the watchdog
/// starts probing again mid-retry and competes with the driver's own re-dial.
/// </summary>
public class DeviceServiceTests
{
    /// <summary>Lets a test raise watchdog transitions and call back into the installed check.</summary>
    private sealed class FakeWatchDog : IWatchDogService
    {
        public Action<DeviceState>? OnDeviceStateChanged { get; set; }

        public Func<string, bool>? IsLinkOwned { get; set; }

        public Task StartWatchDog(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void Raise(DeviceState state) => OnDeviceStateChanged?.Invoke(state);

        public bool Owns(string ip) => IsLinkOwned?.Invoke(ip) == true;
    }

    private static UmecLinkOptions Fast => new()
    {
        ReconnectDelay = TimeSpan.FromMilliseconds(30),
        EchoInterval = TimeSpan.FromMilliseconds(20),
        QueryRefreshInterval = TimeSpan.FromMilliseconds(50),
        MaxFailedSessions = 2,
    };

    private static DeviceState Monitor(string ip = "10.1.2.3") => new()
    {
        Id = 1,
        Name = "uMec10",
        IpAddress = ip,
        Port = "4601",
        State = DeviceStateEnum.Connected,
    };

    private static (DeviceService Service, FakeWatchDog WatchDog) Build(
        FakeReader reader, UmecLinkOptions? options = null)
    {
        var watchDog = new FakeWatchDog();
        var provider = new UmecDeviceDataProvider(
            logger: null,
            options: options ?? Fast,
            readerFactory: _ => reader);

        var service = new DeviceService(watchDog, loggerFactory: null, providers: new[] { provider });
        return (service, watchDog);
    }

    [Fact]
    public void Both_watchdog_hooks_are_installed_by_the_constructor()
    {
        var (_, watchDog) = Build(new FakeReader(FakeReader.StaysOpen));

        // The whole fix hangs off these two being wired; if either is missing the
        // watchdog goes back to probing a port the driver owns.
        Assert.NotNull(watchDog.OnDeviceStateChanged);
        Assert.NotNull(watchDog.IsLinkOwned);
    }

    [Fact]
    public void Nothing_is_owned_before_a_monitor_is_handed_over()
    {
        var (_, watchDog) = Build(new FakeReader(FakeReader.StaysOpen));

        Assert.False(watchDog.Owns("10.1.2.3"));
        Assert.False(watchDog.Owns("not an address"));
    }

    [Fact]
    public void A_monitor_is_owned_from_the_moment_the_link_is_opened()
    {
        // Ownership must not wait for the TCP stream: the provider registers the
        // connection before starting it, and that is what the watchdog asks about.
        var (_, watchDog) = Build(new FakeReader(FakeReader.StaysOpen));

        watchDog.Raise(Monitor());

        Assert.True(watchDog.Owns("10.1.2.3"));
    }

    [Fact]
    public async Task Ownership_lasts_across_the_pause_between_reconnect_attempts()
    {
        // The trap this guards: the provider's connect/disconnect events fire per
        // session, so anything keyed on them would release ownership during the
        // reconnect pause and let the watchdog's probe race the driver's re-dial.
        var reader = new FakeReader(FakeReader.EndsImmediately);
        var (_, watchDog) = Build(reader, Fast with { MaxFailedSessions = 50 });

        watchDog.Raise(Monitor());
        await Wait.UntilAsync(() => reader.Attempts >= 3, "the link has retried twice");

        // Sampled repeatedly, so a release during any pause would be caught.
        for (int i = 0; i < 20; i++)
        {
            Assert.True(watchDog.Owns("10.1.2.3"), $"ownership was dropped mid-retry (sample {i})");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task A_monitor_whose_link_gave_up_is_no_longer_owned()
    {
        // The other half of the contract: suppression must end, or a dead monitor
        // would never be probed again and could never be rediscovered.
        var (_, watchDog) = Build(new FakeReader(FakeReader.Fails));

        watchDog.Raise(Monitor());
        Assert.True(watchDog.Owns("10.1.2.3"));

        await Wait.UntilAsync(() => !watchDog.Owns("10.1.2.3"),
            "ownership is released once the link gives up");
    }

    [Fact]
    public async Task Only_the_address_that_is_linked_is_reported_as_owned()
    {
        var (_, watchDog) = Build(new FakeReader(FakeReader.StaysOpen));

        watchDog.Raise(Monitor("10.1.2.3"));
        await Wait.UntilAsync(() => watchDog.Owns("10.1.2.3"), "the link is registered");

        Assert.False(watchDog.Owns("10.1.2.4"));
    }

    [Fact]
    public void A_disconnected_state_for_an_address_the_driver_does_not_hold_is_ignored()
    {
        var (service, watchDog) = Build(new FakeReader(FakeReader.StaysOpen));

        var unknown = Monitor("10.9.9.9");
        unknown.State = DeviceStateEnum.Disconnected;

        watchDog.Raise(unknown);                    // must not throw

        Assert.Empty(service.GetAllDevice());
    }

    [Fact]
    public async Task Disconnecting_a_monitor_releases_ownership()
    {
        var (_, watchDog) = Build(new FakeReader(FakeReader.StaysOpen));
        var monitor = Monitor();

        watchDog.Raise(monitor);
        await Wait.UntilAsync(() => watchDog.Owns("10.1.2.3"), "the link is up");

        var gone = Monitor();
        gone.State = DeviceStateEnum.Disconnected;
        watchDog.Raise(gone);

        await Wait.UntilAsync(() => !watchDog.Owns("10.1.2.3"), "ownership is released");
    }
}
