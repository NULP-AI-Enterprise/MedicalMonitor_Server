using MedicalMonitorServer.Services;

namespace MedicalMonitorServer.Tests;

/// <summary>
/// The counters behind the uMec feed's once-a-minute Information line. A monitor
/// sends about ten messages a second, so a line per message would bury the alert
/// history that shares the log file; the summary is what stays visible at the
/// default level.
/// </summary>
public class FeedSummaryTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    private const string Monitor = "192.168.2.101";

    [Fact]
    public void Nothing_is_reported_before_the_interval_has_passed()
    {
        var summary = new FeedSummary();

        Assert.Null(summary.Add(Monitor, "P-1", vitals: 3, alarms: 0, Start, Minute));
        Assert.Null(summary.Add(Monitor, "P-1", vitals: 2, alarms: 1, Start.AddSeconds(30), Minute));
        Assert.Null(summary.Add(Monitor, "P-1", vitals: 0, alarms: 0, Start.AddSeconds(59), Minute));
    }

    [Fact]
    public void One_line_sums_the_window_and_the_next_window_starts_empty()
    {
        var summary = new FeedSummary();
        summary.Add(Monitor, "P-1", vitals: 3, alarms: 0, Start, Minute);
        summary.Add(Monitor, "P-1", vitals: 2, alarms: 1, Start.AddSeconds(30), Minute);

        var line = summary.Add(Monitor, "P-1", vitals: 1, alarms: 2, Start.AddSeconds(60), Minute);

        Assert.NotNull(line);
        Assert.Equal(Monitor, line.Value.MonitorIp);
        Assert.Equal("P-1", line.Value.PatientId);
        Assert.Equal(3, line.Value.Messages);
        Assert.Equal(6, line.Value.Vitals);
        Assert.Equal(3, line.Value.Alarms);
        Assert.Equal(0, line.Value.Dropped);

        // The message that closed the window was counted in it, not in the next one.
        Assert.Null(summary.Add(Monitor, "P-1", vitals: 5, alarms: 0, Start.AddSeconds(61), Minute));
        var next = summary.Add(Monitor, "P-1", vitals: 0, alarms: 0, Start.AddSeconds(121), Minute);
        Assert.Equal(2, next!.Value.Messages);
        Assert.Equal(5, next.Value.Vitals);
    }

    [Fact]
    public void Each_monitor_has_its_own_window()
    {
        var summary = new FeedSummary();
        summary.Add("10.0.0.1", "P-1", vitals: 4, alarms: 0, Start, Minute);
        summary.Add("10.0.0.2", "P-2", vitals: 7, alarms: 0, Start.AddSeconds(40), Minute);

        var first = summary.Add("10.0.0.1", "P-1", vitals: 1, alarms: 0, Start.AddSeconds(60), Minute);
        var second = summary.Add("10.0.0.2", "P-2", vitals: 1, alarms: 0, Start.AddSeconds(60), Minute);

        Assert.Equal(5, first!.Value.Vitals);
        Assert.Null(second);                    // its own window opened 40 s later
    }

    [Fact]
    public void A_message_with_data_and_no_patient_counts_as_dropped_not_as_handed_on()
    {
        var summary = new FeedSummary();
        summary.Add(Monitor, patientId: null, vitals: 4, alarms: 1, Start, Minute);

        var line = summary.Add(Monitor, patientId: null, vitals: 2, alarms: 0, Start.AddSeconds(60), Minute);

        Assert.Equal(2, line!.Value.Dropped);
        Assert.Equal(0, line.Value.Vitals);     // nothing reached the pipeline
        Assert.Equal(0, line.Value.Alarms);
        Assert.Null(line.Value.PatientId);
    }

    [Fact]
    public void An_empty_message_from_an_unbound_monitor_is_not_a_drop()
    {
        // The monitor's echo carries nothing, so nothing was lost.
        var summary = new FeedSummary();
        summary.Add(Monitor, patientId: null, vitals: 0, alarms: 0, Start, Minute);

        var line = summary.Add(Monitor, patientId: null, vitals: 0, alarms: 0, Start.AddSeconds(60), Minute);

        Assert.Equal(2, line!.Value.Messages);
        Assert.Equal(0, line.Value.Dropped);
    }

    [Fact]
    public void The_line_reports_how_long_the_window_really_was()
    {
        // There is no timer: a monitor that went quiet closes its window only when
        // it speaks again, so "60 s" would misstate the rate.
        var summary = new FeedSummary();
        summary.Add(Monitor, "P-1", vitals: 1, alarms: 0, Start, Minute);

        var line = summary.Add(Monitor, "P-1", vitals: 1, alarms: 0, Start.AddMinutes(10), Minute);

        Assert.Equal(TimeSpan.FromMinutes(10), line!.Value.Elapsed);
    }

    [Fact]
    public void The_patient_shown_is_the_latest_one_the_monitor_fed()
    {
        var summary = new FeedSummary();
        summary.Add(Monitor, "P-1", vitals: 1, alarms: 0, Start, Minute);
        summary.Add(Monitor, "P-2", vitals: 1, alarms: 0, Start.AddSeconds(10), Minute);

        var line = summary.Add(Monitor, patientId: null, vitals: 0, alarms: 0, Start.AddSeconds(60), Minute);

        Assert.Equal("P-2", line!.Value.PatientId);
    }
}
