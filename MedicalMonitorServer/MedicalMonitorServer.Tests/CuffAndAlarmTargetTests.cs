using MedicalMonitorServer.Contracts.Api;
using MedicalMonitorServer.Contracts.Models;
using MedicalMonitorServer.Core.Monitoring;
using MedicalMonitorServer.DeviceDriver;
using MedicalMonitorServer.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace MedicalMonitorServer.Tests;

/// <summary>
/// What stations are told about two things only the monitor knows: that its cuff is taking a
/// measurement right now, and which value one of its own alarms is on. Both come from a driver
/// whose monitor says so (a Nihon Kohden over LS-NET) and go through the feed as it runs.
/// </summary>
public class CuffAndAlarmTargetTests : IAsyncLifetime
{
    private const string Monitor = "10.10.46.48";

    private readonly FakeProvider _provider = new();
    private readonly PatientRegistry _patients = new();
    private readonly FakeHubContext _hub = new();
    private readonly UmecFeedService _service;
    private readonly CancellationTokenSource _cts = new();

    public CuffAndAlarmTargetTests()
    {
        var bindings = new MonitorBindings();
        var pipeline = new MonitorPipeline(
            _hub, _patients, new AlertStateService(), bindings,
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

    /// <summary>A complete report, as a Nihon Kohden sends one every second.</summary>
    private static UmecMessage Report(bool cuffMeasuring, params (int Id, double? Value)[] values)
    {
        var oru = new UmecVitalsMessage { VitalsAreComplete = true, NibpMeasuring = cuffMeasuring };
        foreach (var (id, value) in values)
        {
            oru.Vitals.Add(new VitalSign { ParameterId = id, Name = $"p{id}", Value = value, RawValue = $"{value}" });
        }

        return new UmecMessage { Kind = UmecMessageKind.Vitals, SourceIp = Monitor, Oru = oru };
    }

    private static UmecMessage Alarms(UmecMessageKind kind, params ActiveAlarm[] alarms)
    {
        var oru = new UmecVitalsMessage();
        oru.Alarms.AddRange(alarms);
        return new UmecMessage { Kind = kind, SourceIp = Monitor, Oru = oru };
    }

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

    /// <summary>The cuff reading in the frame stations were sent last.</summary>
    private BloodPressureDto? LastCuff() =>
        _hub.SentTo<MonitorUpdateDto>(HubMethods.ReceiveVitals).LastOrDefault()?.Vitals.Nibp;

    [Fact]
    public async Task Stations_are_told_while_the_cuff_is_taking_a_measurement()
    {
        // A reading stands: 117/58, mean 77.
        await DeliverUntilAsync(Report(false, (101, 80), (170, 117), (171, 58), (172, 77)),
            () => LastCuff() is { Systolic: 117, Measuring: false }, "the reading is on the stations");

        // The cuff is inflated again; the monitor goes on listing the reading before.
        await DeliverUntilAsync(Report(true, (101, 83), (170, 117), (171, 58), (172, 77)),
            () => LastCuff() is { Measuring: true }, "the stations are told a measurement is running");
        Assert.Equal((117, 58, 77), (LastCuff()!.Systolic, LastCuff()!.Diastolic, LastCuff()!.MeanArterialPressure));

        // Done: the new reading, and nothing running any more.
        await DeliverUntilAsync(Report(false, (101, 82), (170, 122), (171, 76), (172, 91)),
            () => LastCuff() is { Systolic: 122 }, "the new reading is on the stations");
        Assert.False(LastCuff()!.Measuring);
    }

    [Fact]
    public async Task A_first_measurement_shows_as_running_with_no_reading_yet()
    {
        // No reading before it: the monitor lists the cuff pressures without a number meanwhile.
        await DeliverUntilAsync(Report(true, (101, 80), (170, null), (171, null), (172, null)),
            () => LastCuff() is { Measuring: true }, "the stations are told a measurement is running");

        Assert.Equal(new BloodPressureDto(null, null, null, null, Measuring: true), LastCuff());
    }

    [Fact]
    public async Task Samples_that_come_between_two_reports_go_on_saying_it()
    {
        await DeliverUntilAsync(Report(true, (101, 80), (170, null), (171, null), (172, null)),
            () => LastCuff() is { Measuring: true }, "the stations are told a measurement is running");
        int frames = _hub.SentTo<MonitorUpdateDto>(HubMethods.ReceiveVitals).Count;

        // Four packets of samples a second carry no values of their own: the frame they go out
        // in repeats what the last report said.
        var samples = new UmecVitalsMessage();
        samples.ScaledWaves.Add(new ScaledWave { Type = WaveformType.ECG_II, Samples = new float[32], SampleRate = 125 });
        await DeliverUntilAsync(
            new UmecMessage { Kind = UmecMessageKind.Waveforms, SourceIp = Monitor, Oru = samples },
            () => _hub.SentTo<MonitorUpdateDto>(HubMethods.ReceiveVitals).Skip(frames).Any(f => f.Waveforms is { Length: 1 }),
            "the samples are passed on");

        Assert.True(LastCuff()!.Measuring);
    }

    [Fact]
    public async Task A_monitors_alarm_on_a_vital_marks_that_vital_on_the_stations()
    {
        await DeliverUntilAsync(Report(false, (101, 80), (160, 98)),
            () => _patients.GetAll().Count == 1, "the patient is on the roster");

        // The monitor's own SpO2 limit alarm: on the value, so the feed knows which it is.
        await DeliverUntilAsync(
            Alarms(UmecMessageKind.PhysiologicalAlarms, new ActiveAlarm
            {
                Category = AlarmCategory.Physiological, AlarmId = 0x0700, ParameterId = 160,
                Text = "SpO2 out of limits", Level = AlarmLevel.High,
            }),
            () => _hub.SentTo<AlertDto>(HubMethods.AlertTriggered).Count == 1, "the alarm is on the stations");

        var alert = _hub.SentTo<AlertDto>(HubMethods.AlertTriggered).Single();
        Assert.Equal((VitalSignType.SpO2, null, AlertSeverity.Critical, AlertSource.Device),
            (alert.VitalSign, alert.Parameter, alert.Severity, alert.Source));
        Assert.Equal(("SpO2 out of limits", 0x0700), (alert.Message, alert.Code));
    }

    [Fact]
    public async Task One_on_another_value_names_it_and_one_about_no_value_names_none()
    {
        await DeliverUntilAsync(Report(false, (101, 80)),
            () => _patients.GetAll().Count == 1, "the patient is on the roster");

        await DeliverUntilAsync(
            Alarms(UmecMessageKind.PhysiologicalAlarms, new ActiveAlarm
            {
                Category = AlarmCategory.Physiological, AlarmId = 0x2C00, ParameterId = ServerParameterIds.ArtSystolic,
                Text = "ART systolic out of limits", Level = AlarmLevel.Medium,
            }),
            () => _hub.SentTo<AlertDto>(HubMethods.AlertTriggered).Count == 1, "the arterial alarm is on the stations");

        // A technical alarm, and any alarm of a monitor that sends an id and a text (a uMec).
        await DeliverUntilAsync(
            Alarms(UmecMessageKind.TechnicalAlarms, new ActiveAlarm
            {
                Category = AlarmCategory.Technical, AlarmId = 0x10001, Text = "Check electrodes", Level = AlarmLevel.Low,
            }),
            () => _hub.SentTo<AlertDto>(HubMethods.AlertTriggered).Count == 2, "the technical alarm is on the stations");

        var alerts = _hub.SentTo<AlertDto>(HubMethods.AlertTriggered);
        Assert.Equal((null, "art.sys"), (alerts[0].VitalSign, alerts[0].Parameter));
        Assert.Equal((null, null), (alerts[1].VitalSign, alerts[1].Parameter));
    }
}
