using MedicalMonitorServer.Contracts.Models;
using MedicalMonitorServer.Core.Monitoring;
using MedicalMonitorServer.DeviceDriver;
using MedicalMonitorServer.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace MedicalMonitorServer.Tests;

/// <summary>
/// A monitor that reports everything it measures in one go (a Nihon Kohden) tells the server,
/// by leaving a value out, that it no longer has it: the sensor came off. That value must
/// leave the station. A monitor that reports piecemeal (a uMec) says nothing by omission.
/// </summary>
public class CompleteReportTests : IAsyncLifetime
{
    private const string Monitor = "10.10.46.48";

    private readonly FakeProvider _provider = new();
    private readonly PatientRegistry _patients = new();
    private readonly UmecFeedService _service;
    private readonly CancellationTokenSource _cts = new();

    public CompleteReportTests()
    {
        var bindings = new MonitorBindings();
        var pipeline = new MonitorPipeline(
            new FakeHubContext(), _patients, new AlertStateService(), bindings,
            new FeedStatus(), NullLogger<MonitorPipeline>.Instance);
        var router = new MonitorRouter(bindings, pipeline, _patients, NullLogger<MonitorRouter>.Instance);
        var devices = new DeviceService(new FakeWatchDog(), loggerFactory: null, providers: new[] { _provider });

        _service = new UmecFeedService(devices, pipeline, _patients, router, NullLogger<UmecFeedService>.Instance);
    }

    public Task InitializeAsync() => _service.StartAsync(_cts.Token);

    public async Task DisposeAsync()
    {
        _cts.Cancel();
        await _service.StopAsync(CancellationToken.None);
        _service.Dispose();
        _cts.Dispose();
    }

    private static UmecMessage Report(bool complete, params (int Id, double? Value)[] values)
    {
        var oru = new UmecVitalsMessage { VitalsAreComplete = complete };
        foreach (var (id, value) in values)
        {
            oru.Vitals.Add(new VitalSign { ParameterId = id, Name = $"p{id}", Value = value, RawValue = $"{value}" });
        }

        return new UmecMessage { Kind = UmecMessageKind.Vitals, SourceIp = Monitor, Oru = oru };
    }

    private VitalReadings? Readings() =>
        _patients.GetAll().Select(p => _patients.GetReadings(p.PatientId)).FirstOrDefault();

    /// <summary>Delivers until the feed has reacted; see UmecFeedServiceTests for why once may not be enough.</summary>
    private Task DeliverUntilAsync(UmecMessage message, Func<bool> reacted, string because) =>
        Wait.UntilAsync(
            () =>
            {
                if (reacted()) return true;
                _provider.Deliver(message);
                return false;
            },
            because);

    [Fact]
    public async Task A_value_missing_from_a_complete_report_leaves_the_station()
    {
        // HR 101, SpO2 160, PR 161, temperature 200.
        await DeliverUntilAsync(Report(true, (101, 80), (160, 98), (161, 80), (200, 36.5)),
            () => Readings() is { SpO2: 98 }, "the first report is in");

        // The SpO2 probe comes off: the monitor's next report has no SpO2 and no pulse rate.
        await DeliverUntilAsync(Report(true, (101, 81), (200, 36.5)),
            () => Readings() is { HeartRate: 81 }, "the next report is in");

        var readings = Readings()!;
        Assert.Null(readings.SpO2);
        Assert.Null(readings.PulseRate);
        Assert.Equal(36.5, readings.Temperature);
    }

    [Fact]
    public async Task So_does_a_value_the_monitor_lists_without_a_number()
    {
        await DeliverUntilAsync(Report(true, (101, 80), (160, 98)),
            () => Readings() is { SpO2: 98 }, "the first report is in");

        await DeliverUntilAsync(Report(true, (101, 80), (160, null)),
            () => Readings() is { SpO2: null }, "the value is taken off");

        Assert.Equal(80, Readings()!.HeartRate);
    }

    [Fact]
    public async Task A_complete_report_with_nothing_in_it_clears_every_running_value()
    {
        await DeliverUntilAsync(Report(true, (101, 80), (160, 98), (200, 36.5)),
            () => Readings() is { HeartRate: 80 }, "the first report is in");

        await DeliverUntilAsync(Report(true),
            () => Readings() is { HeartRate: null }, "the empty report is applied");

        Assert.Equal(new VitalReadings(), Readings());
    }

    [Fact]
    public async Task A_cuff_reading_stands_until_the_next_one()
    {
        // NIBP 170/171/172 is taken now and then; a report between two measurements may not repeat it.
        await DeliverUntilAsync(Report(true, (101, 80), (170, 136), (171, 75), (172, 95)),
            () => Readings() is { Systolic: 136 }, "the report with the measurement is in");

        await DeliverUntilAsync(Report(true, (101, 82)),
            () => Readings() is { HeartRate: 82 }, "the next report is in");

        var readings = Readings()!;
        Assert.Equal((136, 75, 95), (readings.Systolic, readings.Diastolic, readings.MeanArterialPressure));
    }

    [Fact]
    public async Task A_piecemeal_report_still_only_adds()
    {
        // What a uMec does: each message carries some of the values.
        await DeliverUntilAsync(Report(false, (101, 80), (160, 98)),
            () => Readings() is { SpO2: 98 }, "the first message is in");

        await DeliverUntilAsync(Report(false, (151, 16)),
            () => Readings() is { RespiratoryRate: 16 }, "the second message is in");

        var readings = Readings()!;
        Assert.Equal((80, 98), (readings.HeartRate, readings.SpO2));
    }
}
