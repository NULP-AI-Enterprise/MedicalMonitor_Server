using MedicalMonitorServer.Contracts.Models;
using MedicalMonitorServer.Core.Monitoring;
using MedicalMonitorServer.DeviceDriver;
using MedicalMonitorServer.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MedicalMonitorServer.Tests;

/// <summary>
/// The feed as it really runs: messages from a device provider, through
/// <see cref="DeviceService"/> and the queue, into the router and the pipeline.
/// What is checked here is what an operator sees in the log at the default
/// level — who a monitor is feeding and how much, and how much was dropped.
/// </summary>
public class UmecFeedServiceTests : IAsyncLifetime
{
    private readonly FakeProvider _provider = new();
    private readonly PatientRegistry _patients = new();
    private readonly CapturingLogger<UmecFeedService> _log = new();
    private readonly UmecFeedService _service;
    private readonly CancellationTokenSource _cts = new();

    public UmecFeedServiceTests()
    {
        var bindings = new MonitorBindings();
        var pipeline = new MonitorPipeline(
            new FakeHubContext(), _patients, new AlertStateService(), bindings,
            new FeedStatus(), NullLogger<MonitorPipeline>.Instance);
        var router = new MonitorRouter(bindings, pipeline, _patients, NullLogger<MonitorRouter>.Instance);
        var devices = new DeviceService(new FakeWatchDog(), loggerFactory: null, providers: new[] { _provider });

        _service = new UmecFeedService(devices, pipeline, _patients, router, _log);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        _cts.Cancel();
        await _service.StopAsync(CancellationToken.None);
        _service.Dispose();
        _cts.Dispose();
    }

    private static UmecMessage Vitals(string sourceIp, params (int Id, double? Value)[] values)
    {
        var oru = new UmecVitalsMessage();
        foreach (var (id, value) in values)
        {
            oru.Vitals.Add(new VitalSign { ParameterId = id, Name = $"p{id}", Value = value, RawValue = $"{value}" });
        }

        return new UmecMessage { Kind = UmecMessageKind.Vitals, MessageId = 204, SourceIp = sourceIp, Oru = oru };
    }

    /// <summary>
    /// Delivers the message until the service reacts to it. The host package in use
    /// (Microsoft.Extensions.Hosting.Abstractions 10) starts ExecuteAsync on the
    /// thread pool, so StartAsync returns before the feed has subscribed and a
    /// message delivered straight away may reach nobody.
    /// </summary>
    private Task DeliverUntilAsync(UmecMessage message, Func<bool> reacted, string because) =>
        Wait.UntilAsync(
            () =>
            {
                if (reacted()) return true;
                _provider.Deliver(message);
                return false;
            },
            because);

    private IReadOnlyList<string> Summaries() => _log.Entries
        .Where(e => e.Level == LogLevel.Information && e.Message.StartsWith("uMec feed from"))
        .Select(e => e.Message)
        .ToList();

    [Fact]
    public async Task A_monitors_traffic_is_summed_up_at_Information_with_its_patient()
    {
        _service.SummaryInterval = TimeSpan.Zero;       // every message closes a window
        await _service.StartAsync(_cts.Token);

        // HR and SpO2 measured, one sentinel ("no valid measurement") that is not a value.
        await DeliverUntilAsync(
            Vitals("192.168.2.101", (101, 72), (160, 98), (151, null)),
            () => Summaries().Count >= 1,
            "the summary line is logged");

        // With a zero interval every message closes its own window, so each line is one message.
        var line = Summaries()[0];
        var patient = Assert.Single(_patients.GetAll());

        Assert.Contains("192.168.2.101", line);
        Assert.Contains($"for patient {patient.PatientId}", line);
        Assert.Contains("1 messages", line);
        Assert.Contains("2 measured values", line);
        Assert.Contains("0 messages with data dropped", line);
    }

    [Fact]
    public async Task A_message_that_had_nowhere_to_go_shows_up_as_dropped()
    {
        // No source address and no patient id: the router cannot attribute it. The
        // handler returns early for it, so it must have been counted before that.
        _service.SummaryInterval = TimeSpan.Zero;
        await _service.StartAsync(_cts.Token);

        await DeliverUntilAsync(
            Vitals(sourceIp: "", (101, 72)),
            () => Summaries().Count >= 1,
            "the summary line is logged");
        var line = Summaries()[0];

        Assert.Contains("(unknown)", line);
        Assert.Contains("for patient (none)", line);
        Assert.Contains("1 messages with data dropped", line);
        Assert.Contains("0 measured values", line);
        Assert.Empty(_patients.GetAll());
    }

    [Fact]
    public async Task By_default_a_single_message_logs_nothing_at_Information()
    {
        // The default is a minute: ten messages a second must not become ten lines.
        Assert.Equal(TimeSpan.FromMinutes(1), _service.SummaryInterval);
        await _service.StartAsync(_cts.Token);

        await DeliverUntilAsync(
            Vitals("192.168.2.101", (101, 72)),
            () => _patients.GetAll().Count == 1,
            "the message is processed");
        await Task.Delay(50);

        Assert.Empty(Summaries());
    }
}
