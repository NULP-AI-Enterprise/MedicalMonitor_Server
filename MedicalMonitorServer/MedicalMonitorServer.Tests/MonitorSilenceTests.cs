using MedicalMonitorServer.Contracts.Api;
using MedicalMonitorServer.Contracts.Models;
using MedicalMonitorServer.Core.Monitoring;
using MedicalMonitorServer.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace MedicalMonitorServer.Tests;

/// <summary>
/// A monitor that stops sending. Values only move when a message arrives, so
/// without a check the last numbers would stay on screen as if current and no
/// alert would say otherwise. These tests run the real router, pipeline,
/// registry, bindings and alert store against a recording hub, and move time
/// by handing the sweep a clock.
/// </summary>
public class MonitorSilenceTests
{
    private static readonly TimeSpan SilentAfter = TimeSpan.FromSeconds(30);

    private const string Monitor = "192.168.2.101";

    private readonly FakeHubContext _hub = new();
    private readonly PatientRegistry _patients = new();
    private readonly AlertStateService _alerts = new();
    private readonly MonitorBindings _bindings = new();
    private readonly MonitorPipeline _pipeline;
    private readonly MonitorRouter _router;

    public MonitorSilenceTests()
    {
        _pipeline = new MonitorPipeline(
            _hub, _patients, _alerts, _bindings, new FeedStatus(), NullLogger<MonitorPipeline>.Instance);
        _router = new MonitorRouter(_bindings, _pipeline, _patients, NullLogger<MonitorRouter>.Instance);
    }

    private static PatientInfo Pid(string patientId) =>
        new() { PidPresent = true, PatientId = patientId, LastName = "Doe", FirstName = "Jane" };

    /// <summary>A message from the monitor right now: binds the patient and stamps last-seen.</summary>
    private async Task<string> MessageAsync(string monitorIp = Monitor, string? patientId = "P-1")
    {
        var resolved = await _router.ResolveAsync(
            monitorIp, patientId is null ? null : Pid(patientId), hasReadings: true, CancellationToken.None);
        return resolved!;
    }

    /// <summary>The moment the test began; every later time is this plus some seconds.</summary>
    private readonly DateTimeOffset _start = DateTimeOffset.UtcNow;

    /// <summary>Runs the check as if it were this long after the start of the test.</summary>
    private Task SweepAsync(TimeSpan sinceStart) =>
        _router.SweepSilentAsync(_start + sinceStart, SilentAfter, CancellationToken.None);

    /// <summary>
    /// The monitor is heard from at a chosen time. The router stamps last-seen with the
    /// real clock, so later messages are placed on the test's timeline through the
    /// bindings directly; reporting the same patient changes nothing else.
    /// </summary>
    private void HeardAt(TimeSpan sinceStart, string monitorIp = Monitor, string patientId = "P-1") =>
        _bindings.Report(monitorIp, patientId, 0, hasReadings: true, _start + sinceStart);

    private AlertDto? SilenceAlert(string patientId = "P-1") =>
        _alerts.GetActiveAlerts().FirstOrDefault(a => a.AlertId == AlertDto.BuildMonitorSilentId(patientId));

    private int Sent(string method) => _hub.Sent.Count(s => s.Method == method);

    // ------------------------------------------------------------ going silent

    [Fact]
    public async Task A_monitor_silent_past_the_threshold_raises_one_critical_technical_alert()
    {
        await MessageAsync();

        await SweepAsync(TimeSpan.FromSeconds(31));

        var alert = Assert.Single(_hub.SentTo<AlertDto>(HubMethods.AlertTriggered));
        Assert.Equal(AlertDto.BuildMonitorSilentId("P-1"), alert.AlertId);
        Assert.Equal("P-1", alert.PatientId);
        Assert.Equal(AlertSeverity.Critical, alert.Severity);
        Assert.Equal(AlertCategory.Technical, alert.Category);
        Assert.Equal(AlertSource.Device, alert.Source);
        Assert.Equal(AlertDto.MonitorSilentCode, alert.Code);
        Assert.Contains(Monitor, alert.Message);
        Assert.NotNull(SilenceAlert());
    }

    [Fact]
    public async Task The_last_values_are_dropped_and_stations_are_sent_a_frame_of_nulls()
    {
        var patientId = await MessageAsync();
        await _pipeline.IngestReadingsAsync(
            patientId, new VitalReadings(HeartRate: 72, SpO2: 98, Systolic: 120, Diastolic: 80),
            null, DateTimeOffset.UtcNow);
        int framesBefore = Sent(HubMethods.ReceiveVitals);

        await SweepAsync(TimeSpan.FromSeconds(31));

        Assert.Null(_patients.GetReadings(patientId));
        Assert.Equal(framesBefore + 1, Sent(HubMethods.ReceiveVitals));
        var frame = _hub.SentTo<MonitorUpdateDto>(HubMethods.ReceiveVitals)[^1];
        Assert.Equal(patientId, frame.PatientId);
        Assert.Null(frame.Vitals.HeartRate);
        Assert.Null(frame.Vitals.SpO2);
        Assert.Null(frame.Vitals.RespiratoryRate);
        Assert.Null(frame.Vitals.Temperature);
        Assert.Null(frame.Vitals.PulseRate);
        Assert.Null(frame.Vitals.Nibp?.Systolic);
        Assert.Null(frame.Vitals.Nibp?.Diastolic);
    }

    [Fact]
    public async Task A_monitor_that_spoke_within_the_threshold_is_left_alone()
    {
        var patientId = await MessageAsync();
        await _pipeline.IngestReadingsAsync(patientId, new VitalReadings(HeartRate: 72), null, DateTimeOffset.UtcNow);

        await SweepAsync(TimeSpan.FromSeconds(29));

        Assert.Null(SilenceAlert());
        Assert.Equal(72, _patients.GetReadings(patientId)?.HeartRate);
    }

    [Fact]
    public async Task Repeated_sweeps_of_the_same_silence_say_nothing_more_and_keep_the_acknowledgement()
    {
        await MessageAsync();
        await SweepAsync(TimeSpan.FromSeconds(31));
        _alerts.Acknowledge(AlertDto.BuildMonitorSilentId("P-1"));
        int sentBefore = _hub.Sent.Count;

        await SweepAsync(TimeSpan.FromSeconds(36));
        await SweepAsync(TimeSpan.FromSeconds(41));

        Assert.Equal(sentBefore, _hub.Sent.Count);
        Assert.True(SilenceAlert()!.IsAcknowledged);
    }

    [Fact]
    public async Task The_patients_other_alerts_are_not_dismissed_by_the_silence()
    {
        // A critical clinical alarm must not quietly turn into a technical one.
        var patientId = await MessageAsync();
        await _pipeline.IngestReadingsAsync(patientId, new VitalReadings(HeartRate: 30), null, DateTimeOffset.UtcNow);
        Assert.Contains(_alerts.GetActiveAlerts(), a => a.VitalSign == VitalSignType.HeartRate);

        await SweepAsync(TimeSpan.FromSeconds(31));

        Assert.Contains(_alerts.GetActiveAlerts(), a => a.VitalSign == VitalSignType.HeartRate);
        Assert.NotNull(SilenceAlert());
        Assert.Equal(0, Sent(HubMethods.AlertCleared));
    }

    [Fact]
    public async Task A_monitor_with_no_patient_is_not_flagged()
    {
        // Seen, but it never sent values, so nobody was bound to it.
        await _router.ResolveAsync(Monitor, info: null, hasReadings: false, CancellationToken.None);

        await SweepAsync(TimeSpan.FromSeconds(120));

        Assert.Empty(_alerts.GetActiveAlerts());
        Assert.Empty(_hub.Sent);
    }

    // ------------------------------------------------------------ coming back

    [Fact]
    public async Task A_monitor_that_speaks_again_has_its_alert_cleared_once()
    {
        await MessageAsync();
        await SweepAsync(TimeSpan.FromSeconds(31));
        Assert.NotNull(SilenceAlert());

        HeardAt(TimeSpan.FromSeconds(35));
        await SweepAsync(TimeSpan.FromSeconds(36));
        await SweepAsync(TimeSpan.FromSeconds(41));

        Assert.Null(SilenceAlert());
        var cleared = Assert.Single(_hub.SentTo<AlertDto>(HubMethods.AlertCleared));
        Assert.Equal(AlertDto.BuildMonitorSilentId("P-1"), cleared.AlertId);
    }

    [Fact]
    public async Task A_second_silence_after_a_return_alerts_again()
    {
        await MessageAsync();
        await SweepAsync(TimeSpan.FromSeconds(31));
        HeardAt(TimeSpan.FromSeconds(35));
        await SweepAsync(TimeSpan.FromSeconds(36));

        await SweepAsync(TimeSpan.FromSeconds(70));     // 35 s after it was last heard

        Assert.Equal(2, Sent(HubMethods.AlertTriggered));
        Assert.NotNull(SilenceAlert());
    }

    [Fact]
    public async Task A_patient_the_returning_monitor_no_longer_names_leaves_with_their_alert()
    {
        // The monitor went silent with P-1 on it and came back admitted to P-2: P-1 was
        // discharged on it, and their alert must not outlive them.
        await MessageAsync(patientId: "P-1");
        await SweepAsync(TimeSpan.FromSeconds(31));
        Assert.NotNull(SilenceAlert("P-1"));

        await MessageAsync(patientId: "P-2");
        HeardAt(TimeSpan.FromSeconds(35), patientId: "P-2");
        await SweepAsync(TimeSpan.FromSeconds(36));

        Assert.Null(SilenceAlert("P-1"));
        Assert.Null(SilenceAlert("P-2"));
        Assert.Null(_patients.Get("P-1"));              // discharged on the monitor
    }

    [Fact]
    public async Task Removing_a_patient_while_silent_takes_the_alert_and_it_does_not_come_back()
    {
        await MessageAsync();
        await SweepAsync(TimeSpan.FromSeconds(31));

        await _pipeline.RemovePatientAsync("P-1");
        await SweepAsync(TimeSpan.FromSeconds(36));

        Assert.Empty(_alerts.GetActiveAlerts());
    }

    [Fact]
    public async Task Each_monitor_is_judged_on_its_own_silence()
    {
        await MessageAsync("10.0.0.1", "P-1");
        await MessageAsync("10.0.0.2", "P-2");
        await SweepAsync(TimeSpan.FromSeconds(31));
        Assert.NotNull(SilenceAlert("P-1"));
        Assert.NotNull(SilenceAlert("P-2"));

        HeardAt(TimeSpan.FromSeconds(35), "10.0.0.2", "P-2");   // only the second one returns
        await SweepAsync(TimeSpan.FromSeconds(36));

        Assert.Null(SilenceAlert("P-2"));
        Assert.NotNull(SilenceAlert("P-1"));
    }
}
