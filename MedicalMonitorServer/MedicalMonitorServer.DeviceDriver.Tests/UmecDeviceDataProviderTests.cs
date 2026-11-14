using MedicalMonitorServer.Contracts.Models;
using MedicalMonitorServer.DeviceDriver.Umec;

namespace MedicalMonitorServer.DeviceDriver.Tests;

/// <summary>
/// How the provider keeps its book of live links: adding one, closing one, and
/// above all letting go of a link that died, so the watchdog can hand the
/// monitor over again.
/// </summary>
public class UmecDeviceDataProviderTests
{
    private static UmecLinkOptions FastOptions => new()
    {
        ReconnectDelay = TimeSpan.FromMilliseconds(10),
        EchoInterval = TimeSpan.FromMilliseconds(20),
        MaxFailedSessions = 2,
    };

    private const string Vitals = "MSH|^~\\&|||||||ORU^R01|77|P|2.3.1|\rPID|||12345||DOE^JANE||19700101|F\r";

    private static DeviceState Monitor(string ip = "10.0.0.5") => new()
    {
        Id = 1,
        Name = "uMec10",
        IpAddress = ip,
        Port = "4601",
        State = DeviceStateEnum.Connected,
    };

    private static UmecDeviceDataProvider Provider(FakeReader reader, UmecLinkOptions? options = null) =>
        new(logger: null, options: options ?? FastOptions, readerFactory: _ => reader);

    [Fact]
    public async Task Connecting_opens_a_link_and_lists_the_device()
    {
        var reader = new FakeReader(FakeReader.StaysOpen);
        using var provider = Provider(reader);
        var monitor = Monitor();

        provider.Connect(monitor);

        Assert.Equal(new[] { monitor }, provider.ConnectedDevices);
        await Wait.UntilAsync(() => reader.Attempts == 1, "the link is dialled");
    }

    [Fact]
    public async Task An_open_stream_raises_OnDeviceConnected()
    {
        var reader = new FakeReader(FakeReader.StaysOpen);
        using var provider = Provider(reader);
        var seen = new List<DeviceState>();
        provider.OnDeviceConnected += (_, state) => { lock (seen) { seen.Add(state); } };

        var monitor = Monitor();
        provider.Connect(monitor);

        await Wait.UntilAsync(() => { lock (seen) { return seen.Count == 1; } }, "the device is reported connected");
        lock (seen) { Assert.Equal(new[] { monitor }, seen); }
    }

    [Fact]
    public async Task A_dropped_stream_raises_OnDeviceDisconnected_and_the_reconnect_reports_it_up_again()
    {
        var reader = new FakeReader(FakeReader.DeliversThenEnds(Vitals), FakeReader.StaysOpen);
        using var provider = Provider(reader);
        var events = new List<string>();
        provider.OnDeviceConnected += (_, _) => { lock (events) { events.Add("up"); } };
        provider.OnDeviceDisconnected += (_, _) => { lock (events) { events.Add("down"); } };

        provider.Connect(Monitor());

        await Wait.UntilAsync(() => { lock (events) { return events.Count == 3; } }, "the link drops and comes back");
        lock (events) { Assert.Equal(new[] { "up", "down", "up" }, events); }
    }

    [Fact]
    public async Task Connecting_the_same_address_twice_opens_one_link()
    {
        var reader = new FakeReader(FakeReader.StaysOpen);
        using var provider = Provider(reader);

        provider.Connect(Monitor());
        provider.Connect(Monitor());

        await Wait.UntilAsync(() => reader.Attempts == 1, "the link is dialled");
        await Task.Delay(50);
        Assert.Equal(1, reader.Attempts);
        Assert.Single(provider.ConnectedDevices);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_device_without_an_address_is_rejected(string? ip)
    {
        using var provider = Provider(new FakeReader(FakeReader.StaysOpen));
        var monitor = Monitor();
        monitor.IpAddress = ip!;

        Assert.Throws<ArgumentException>(() => provider.Connect(monitor));
    }

    [Fact]
    public void A_device_without_a_numeric_port_is_rejected()
    {
        using var provider = Provider(new FakeReader(FakeReader.StaysOpen));
        var monitor = Monitor();
        monitor.Port = "not a port";

        Assert.Throws<ArgumentException>(() => provider.Connect(monitor));
    }

    // ------------------------------------------------- letting go of a link

    [Fact]
    public async Task A_link_that_gives_up_is_dropped_from_the_connected_devices()
    {
        var reader = new FakeReader(FakeReader.Fails);
        using var provider = Provider(reader);

        provider.Connect(Monitor());

        await Wait.UntilAsync(() => provider.ConnectedDevices.Count == 0,
            "the dead link is removed from the book");
    }

    [Fact]
    public async Task A_link_that_gives_up_marks_the_device_disconnected_for_the_watchdog()
    {
        var reader = new FakeReader(FakeReader.Fails);
        using var provider = Provider(reader);
        var monitor = Monitor();

        provider.Connect(monitor);

        // The watchdog only reports state *changes*; without this the monitor
        // would look connected forever and never be handed over again.
        await Wait.UntilAsync(() => monitor.State == DeviceStateEnum.Disconnected,
            "the device is marked disconnected");
    }

    [Fact]
    public async Task The_same_monitor_can_be_connected_again_after_its_link_died()
    {
        // The reason OnConnectionClosed exists: a stale entry would make
        // Connect() a no-op and keep the monitor offline for good.
        var reader = new FakeReader(FakeReader.Fails);
        using var provider = Provider(reader);
        var monitor = Monitor();

        provider.Connect(monitor);
        await Wait.UntilAsync(() => provider.ConnectedDevices.Count == 0, "the first link gives up");

        int attemptsAfterFirstLink = reader.Attempts;
        provider.Connect(monitor);

        Assert.Single(provider.ConnectedDevices);
        await Wait.UntilAsync(() => reader.Attempts > attemptsAfterFirstLink,
            "the second link dials the monitor again");
    }

    [Fact]
    public async Task Disconnecting_does_not_mark_the_device_disconnected()
    {
        // We closed it on purpose, so the watchdog's view must not be touched.
        var reader = new FakeReader(FakeReader.StaysOpen);
        using var provider = Provider(reader);
        var monitor = Monitor();

        provider.Connect(monitor);
        await Wait.UntilAsync(() => reader.Attempts == 1, "the link is up");
        provider.Disconnect(monitor);

        await Task.Delay(100);
        Assert.Empty(provider.ConnectedDevices);
        Assert.Equal(DeviceStateEnum.Connected, monitor.State);
        Assert.Equal(1, reader.Attempts);           // and no reconnect
    }

    [Fact]
    public void Disconnecting_a_monitor_that_was_never_connected_is_harmless()
    {
        using var provider = Provider(new FakeReader(FakeReader.StaysOpen));

        provider.Disconnect(Monitor());

        Assert.Empty(provider.ConnectedDevices);
    }

    [Fact]
    public async Task Disposing_the_provider_closes_every_link()
    {
        var first = new FakeReader(FakeReader.StaysOpen);
        var second = new FakeReader(FakeReader.StaysOpen);
        var readers = new Queue<FakeReader>(new[] { first, second });
        var provider = new UmecDeviceDataProvider(
            logger: null, options: FastOptions, readerFactory: _ => readers.Dequeue());

        provider.Connect(Monitor("10.0.0.1"));
        provider.Connect(Monitor("10.0.0.2"));
        await Wait.UntilAsync(() => first.Attempts == 1 && second.Attempts == 1, "both links are up");

        provider.Dispose();

        Assert.Empty(provider.ConnectedDevices);
        await Wait.UntilAsync(
            () => first.Sessions[0].Token.IsCancellationRequested
                && second.Sessions[0].Token.IsCancellationRequested,
            "both sessions are cancelled");
    }

    // ---------------------------------------------------------- the patients

    [Fact]
    public async Task A_received_message_is_published_and_its_patient_remembered()
    {
        var reader = new FakeReader(FakeReader.Delivers(Vitals));
        using var provider = Provider(reader);
        var published = new List<UmecMessage>();
        provider.OnMessageReceived += message => { lock (published) { published.Add(message); } };

        provider.Connect(Monitor());
        await Wait.UntilAsync(() => published.Count == 1, "the message is published");

        Assert.Equal("ORU^R01", published[0].MessageType);
        var patient = Assert.Single(provider.Patients);
        Assert.Equal("12345", patient.PatientId);
    }

    [Fact]
    public async Task A_received_message_is_stamped_with_the_monitor_it_came_from()
    {
        // The feed attributes PID-less messages by this address; without it they are dropped.
        var reader = new FakeReader(FakeReader.Delivers(Vitals));
        using var provider = Provider(reader);
        var published = new List<UmecMessage>();
        provider.OnMessageReceived += message => { lock (published) { published.Add(message); } };

        provider.Connect(Monitor("10.0.0.7"));
        await Wait.UntilAsync(() => published.Count == 1, "the message is published");

        Assert.Equal("10.0.0.7", published[0].SourceIp);
    }

    [Fact]
    public async Task A_patient_seen_twice_is_stored_once()
    {
        var reader = new FakeReader(FakeReader.Delivers(Vitals, Vitals));
        using var provider = Provider(reader);
        int published = 0;
        provider.OnMessageReceived += _ => Interlocked.Increment(ref published);

        provider.Connect(Monitor());
        await Wait.UntilAsync(() => Volatile.Read(ref published) == 2, "both messages arrive");

        Assert.Single(provider.Patients);
    }
}
