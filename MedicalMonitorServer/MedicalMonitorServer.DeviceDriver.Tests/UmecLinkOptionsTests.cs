using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using MedicalMonitorServer.Contracts.Models;
using MedicalMonitorServer.DeviceDriver;
using MedicalMonitorServer.DeviceDriver.Umec;
using Microsoft.Extensions.Configuration;

namespace MedicalMonitorServer.DeviceDriver.Tests;

/// <summary>
/// The link timeouts: their defaults, the fact that they reach the reader, and
/// that the configuration section really binds. Since the watchdog no longer
/// probes a port the driver owns, these timeouts are what notices a monitor
/// that went away, so a silent drift in them would be a silent regression.
/// </summary>
public class UmecLinkOptionsTests
{
    [Fact]
    public void The_defaults_are_ten_seconds_of_silence_and_five_to_connect()
    {
        var options = new UmecLinkOptions();

        // Ten missed echoes: the monitor sends one a second.
        Assert.Equal(TimeSpan.FromSeconds(10), options.ReadTimeout);
        Assert.Equal(TimeSpan.FromSeconds(5), options.ConnectTimeout);
    }

    [Fact]
    public void The_reader_defaults_match_the_link_options()
    {
        // Two independent literals in two namespaces; this is what stops them drifting.
        var reader = new ComunicationService();
        var options = new UmecLinkOptions();

        Assert.Equal(options.ReadTimeout, reader.ReadTimeout);
        Assert.Equal(options.ConnectTimeout, reader.ConnectTimeout);
    }

    [Fact]
    public void The_configuration_section_binds_every_timing()
    {
        // TimeSpan binds from "hh:mm:ss" only. A bare number throws, and a bare
        // numeric *string* silently means days, so the appsettings form is pinned here.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Umec:ReconnectDelay"] = "00:00:03",
                ["Umec:EchoInterval"] = "00:00:04",
                ["Umec:QueryRefreshInterval"] = "00:00:11",
                ["Umec:ReadTimeout"] = "00:00:12",
                ["Umec:ConnectTimeout"] = "00:00:06",
                ["Umec:MaxFailedSessions"] = "7",
            })
            .Build();

        var options = new UmecLinkOptions();
        configuration.GetSection("Umec").Bind(options);

        Assert.Equal(TimeSpan.FromSeconds(3), options.ReconnectDelay);
        Assert.Equal(TimeSpan.FromSeconds(4), options.EchoInterval);
        Assert.Equal(TimeSpan.FromSeconds(11), options.QueryRefreshInterval);
        Assert.Equal(TimeSpan.FromSeconds(12), options.ReadTimeout);
        Assert.Equal(TimeSpan.FromSeconds(6), options.ConnectTimeout);
        Assert.Equal(7, options.MaxFailedSessions);
    }

    [Fact]
    public void An_absent_section_leaves_the_defaults_alone()
    {
        var configuration = new ConfigurationBuilder().Build();
        var options = new UmecLinkOptions();

        configuration.GetSection("Umec").Bind(options);

        Assert.Equal(TimeSpan.FromSeconds(10), options.ReadTimeout);
        Assert.Equal(5, options.MaxFailedSessions);
    }

    [Fact]
    public async Task The_configured_read_timeout_reaches_the_reader()
    {
        // Observed from outside, through the default reader factory: a monitor that
        // accepts and then says nothing must have its session timed out and retried.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cts = new CancellationTokenSource();

        int accepted = 0;
        var clients = new List<TcpClient>();
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    var client = await listener.AcceptTcpClientAsync(cts.Token);
                    lock (clients) { clients.Add(client); }
                    Interlocked.Increment(ref accepted);     // accepted, then silence
                }
                catch (Exception)
                {
                    return;
                }
            }
        });

        using var provider = new UmecDeviceDataProvider(
            logger: null,
            options: new UmecLinkOptions
            {
                ReadTimeout = TimeSpan.FromMilliseconds(150),
                ReconnectDelay = TimeSpan.FromMilliseconds(20),
                EchoInterval = TimeSpan.FromMilliseconds(50),
                MaxFailedSessions = 10,
            });

        provider.Connect(new DeviceState
        {
            Id = 1, Name = "uMec10", IpAddress = "127.0.0.1", Port = port.ToString(),
            State = DeviceStateEnum.Connected,
        });

        await Wait.UntilAsync(() => Volatile.Read(ref accepted) >= 2,
            "the silent session timed out and was retried");

        cts.Cancel();
        listener.Stop();
        lock (clients) { foreach (var c in clients) c.Dispose(); }
    }

    /// <summary>
    /// Depends on the host's network stack: a sandbox with no route to TEST-NET-1
    /// refuses instantly instead of black-holing, which still fails fast but does
    /// not exercise the deadline. Excluded with <c>--filter Network!=true</c>.
    /// </summary>
    [Fact]
    [Trait("Network", "true")]
    public async Task A_connect_that_is_never_answered_fails_fast()
    {
        // 192.0.2.1 is RFC 5737 TEST-NET-1: routable in form, answered by nobody.
        var reader = new ComunicationService { ConnectTimeout = TimeSpan.FromMilliseconds(200) };
        var stopwatch = Stopwatch.StartNew();

        var failure = await Assert.ThrowsAnyAsync<Exception>(() =>
            reader.ReadTcpStreamAsync("192.0.2.1", 4601, (_, _) => Task.CompletedTask));

        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"the connect must not outlive its deadline; took {stopwatch.Elapsed}");

        // When it really black-holed, the deadline is what fired.
        if (failure is TimeoutException)
        {
            Assert.Contains("192.0.2.1", failure.Message);
        }
    }
}
