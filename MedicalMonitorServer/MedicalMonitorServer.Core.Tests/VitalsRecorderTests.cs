using MedicalMonitorServer.Contracts.Api;
using MedicalMonitorServer.Core.Monitoring;
using MedicalMonitorServer.Dal.History;

namespace MedicalMonitorServer.Core.Tests;

/// <summary>
/// What goes into the history: one row per admitted patient per second while their monitor
/// is talking, carrying what a station showed then, and a cuff reading once. Time is the
/// test's own: messages come in, and seconds are drained, at the moments it names.
/// </summary>
public class VitalsRecorderTests
{
    private const string Patient = "0b9c5e2a-6d0f-4c56-9f0e-3a2f8f1d7c11";
    private const int Num = 7;

    private static readonly DateTimeOffset Start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = Start;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly Clock _clock = new();
    private readonly VitalsRecorder _recorder;

    public VitalsRecorderTests() => _recorder = new VitalsRecorder(_clock);

    private static DateTimeOffset At(double seconds) => Start.AddSeconds(seconds);

    private static long Second(double seconds) => At(seconds).ToUnixTimeSeconds();

    /// <summary>A message from the patient's monitor, so many seconds after the start.</summary>
    private void Message(double seconds, VitalReadings snapshot, string patientId = Patient)
    {
        _clock.Now = At(seconds);
        _recorder.Observe(patientId, snapshot);
    }

    /// <summary>The once-a-second write, so many seconds after the start.</summary>
    private IReadOnlyList<VitalsRow> Tick(double seconds) => _recorder.Drain(At(seconds));

    private static VitalReadings Cuff(int systolic, int diastolic, double? takenAt = null, int? heartRate = 80) => new(
        HeartRate: heartRate,
        Systolic: systolic,
        Diastolic: diastolic,
        MeanArterialPressure: (systolic + 2 * diastolic) / 3,
        NibpMeasuredAt: takenAt is { } seconds ? At(seconds) : null);

    [Fact]
    public void A_patient_who_is_not_admitted_is_not_recorded()
    {
        Message(0.5, new VitalReadings(HeartRate: 80), patientId: "ANON-12345678");

        Assert.Empty(Tick(1));
    }

    [Fact]
    public void One_row_a_second_carries_the_latest_values()
    {
        _recorder.Start(Patient, Num, lastCuff: null);

        // A monitor sends several messages within a second; the row is how they left the patient.
        Message(0.2, new VitalReadings(HeartRate: 80, SpO2: 97));
        Message(0.7, new VitalReadings(HeartRate: 82, SpO2: 98, RespiratoryRate: 12, Temperature: 36.5, PulseRate: 81));

        var row = Assert.Single(Tick(1.2));
        Assert.Equal(
            new VitalsRow(Num, Second(1), 82, 98, 81, 12, 36.5, Cuff: null, Extra: null),
            row);
    }

    [Fact]
    public void A_second_the_monitor_skipped_is_filled_with_what_it_said_last()
    {
        _recorder.Start(Patient, Num, null);

        // Once a second is not on the second: this one's report comes a moment after the tick.
        Message(0.6, new VitalReadings(HeartRate: 80));
        Assert.Equal(80, Assert.Single(Tick(1.0)).HeartRate);

        var filled = Assert.Single(Tick(2.0));
        Assert.Equal(Second(2), filled.Timestamp);
        Assert.Equal(80, filled.HeartRate);

        Message(2.1, new VitalReadings(HeartRate: 81));
        Message(2.9, new VitalReadings(HeartRate: 82));
        var next = Assert.Single(Tick(3.0));
        Assert.Equal(Second(3), next.Timestamp);
        Assert.Equal(82, next.HeartRate);
    }

    [Fact]
    public void A_monitor_that_stays_silent_leaves_a_gap()
    {
        _recorder.Start(Patient, Num, null);
        Message(0.6, new VitalReadings(HeartRate: 80));
        Assert.Single(Tick(1.0));
        Assert.Single(Tick(2.0));   // a second or two a talking monitor may skip
        Assert.Single(Tick(3.0));

        // Nothing for longer than that: a gap in the history, not the last values again.
        Assert.Empty(Tick(4.0));
        Assert.Empty(Tick(5.0));
        Assert.Empty(Tick(60.0));

        Message(60.5, new VitalReadings(HeartRate: 81));
        Assert.Equal(Second(61), Assert.Single(Tick(61.0)).Timestamp);
    }

    [Fact]
    public void What_came_in_is_written_however_late_the_tick_is()
    {
        _recorder.Start(Patient, Num, null);
        Message(0.6, new VitalReadings(HeartRate: 80));

        // The disk stalled, the machine was busy: the values were never written, so they are
        // not "old news" yet.
        var row = Assert.Single(Tick(9.0));

        Assert.Equal(Second(9), row.Timestamp);
        Assert.Equal(80, row.HeartRate);
        Assert.Empty(Tick(10.0));
    }

    [Fact]
    public void A_value_that_is_not_measured_is_empty_in_the_row()
    {
        _recorder.Start(Patient, Num, null);

        // A complete report with every sensor off is still the monitor talking.
        Message(0.5, new VitalReadings());

        var row = Assert.Single(Tick(1));
        Assert.Equal(new VitalsRow(Num, Second(1), null, null, null, null, null, null, null), row);
    }

    [Fact]
    public void The_other_values_go_in_as_one_object_in_the_order_of_their_codes()
    {
        _recorder.Start(Patient, Num, null);
        Message(0.5, new VitalReadings(
            HeartRate: 80,
            Parameters: new Dictionary<string, ParameterDto>
            {
                ["ecg.st.ii"] = new("ecg.st.ii", "ST-II", 0.05, "mV", "ecg"),
                ["art.sys"] = new("art.sys", "ART Sys", 118, "mmHg", "art"),
                ["temp.2"] = new("temp.2", "T2", 37, "°C", "temp"),
            }));

        var row = Assert.Single(Tick(1));
        Assert.Equal("""{"art.sys":118,"ecg.st.ii":0.05,"temp.2":37}""", row.Extra);
    }

    [Fact]
    public void A_cuff_reading_is_written_once()
    {
        _recorder.Start(Patient, Num, null);

        // The reading stands on the monitor, and in every report, until the next one.
        Message(0.5, Cuff(120, 80, takenAt: 0.5));
        var first = Assert.Single(Tick(1));
        Assert.Equal(new CuffReading(120, 80, 93, Second(0.5)), first.Cuff);

        Message(1.5, Cuff(120, 80, takenAt: 0.5, heartRate: 81));
        var second = Assert.Single(Tick(2));
        Assert.Null(second.Cuff);
        Assert.Equal(81, second.HeartRate);

        Message(300.5, Cuff(118, 76, takenAt: 300));
        Assert.Equal(new CuffReading(118, 76, 90, Second(300)), Assert.Single(Tick(301)).Cuff);
    }

    [Fact]
    public void A_cuff_reading_is_not_repeated_in_a_second_that_is_filled()
    {
        _recorder.Start(Patient, Num, null);
        Message(0.6, Cuff(120, 80, takenAt: 0.5));
        Assert.NotNull(Assert.Single(Tick(1.0)).Cuff);

        var filled = Assert.Single(Tick(2.0));

        Assert.Equal(80, filled.HeartRate);
        Assert.Null(filled.Cuff);
    }

    [Fact]
    public void The_reading_on_the_monitor_at_admission_is_not_the_new_patients()
    {
        // Taken before the admission, of whoever was on the monitor then.
        var standing = VitalsRecorder.CuffOf(Cuff(150, 95, takenAt: -600));
        _recorder.Start(Patient, Num, standing);

        Message(0.5, Cuff(150, 95, takenAt: -600));
        Assert.Null(Assert.Single(Tick(1)).Cuff);

        Message(60.5, Cuff(121, 79, takenAt: 60));
        Assert.Equal(121, Assert.Single(Tick(61)).Cuff!.Systolic);
    }

    [Fact]
    public void The_same_numbers_with_a_later_time_are_a_new_reading()
    {
        _recorder.Start(Patient, Num, null);
        Message(0.5, Cuff(120, 80, takenAt: 0));
        Tick(1);

        Message(300.5, Cuff(120, 80, takenAt: 300));

        Assert.Equal(Second(300), Assert.Single(Tick(301)).Cuff!.MeasuredAt);
    }

    [Fact]
    public void The_same_numbers_with_a_time_a_few_seconds_off_are_the_same_reading()
    {
        _recorder.Start(Patient, Num, null);
        Message(54.5, Cuff(129, 88, takenAt: 54));
        Assert.Equal(Second(54), Assert.Single(Tick(55)).Cuff!.MeasuredAt);

        // The driver has worked out the moment of the measurement more exactly. A second
        // measurement could not have finished this soon.
        Message(57.5, Cuff(129, 88, takenAt: 57));
        Assert.Null(Assert.Single(Tick(58)).Cuff);

        Message(58.5, Cuff(129, 88, takenAt: 51));
        Assert.Null(Assert.Single(Tick(59)).Cuff);
    }

    [Fact]
    public void The_same_numbers_without_their_time_are_the_same_reading()
    {
        _recorder.Start(Patient, Num, null);
        Message(0.5, Cuff(120, 80, takenAt: 0));
        Tick(1);

        // A monitor whose stream restarted shows the reading it had, and no longer says when.
        Message(1.5, Cuff(120, 80, takenAt: null));

        Assert.Null(Assert.Single(Tick(2)).Cuff);
    }

    [Fact]
    public void Two_ticks_in_one_second_write_one_row()
    {
        _recorder.Start(Patient, Num, null);

        Message(0.9, new VitalReadings(HeartRate: 80));
        Assert.Equal(Second(1), Assert.Single(Tick(1.05)).Timestamp);

        // The timer came early: what arrived meanwhile waits for the next second.
        Message(1.5, new VitalReadings(HeartRate: 81));
        Assert.Empty(Tick(1.95));

        var next = Assert.Single(Tick(2.01));
        Assert.Equal(Second(2), next.Timestamp);
        Assert.Equal(81, next.HeartRate);
    }

    [Fact]
    public void A_patient_who_was_discharged_is_recorded_no_more()
    {
        _recorder.Start(Patient, Num, null);
        Message(0.5, new VitalReadings(HeartRate: 80));

        _recorder.Stop(Patient);

        // Not even what had come in before: it would land in a second after the discharge.
        Assert.Empty(Tick(1));
        Message(1.5, new VitalReadings(HeartRate: 81));
        Assert.Empty(Tick(2));
    }

    [Fact]
    public void On_another_monitor_what_was_not_written_and_the_reading_it_shows_are_not_theirs()
    {
        _recorder.Start(Patient, Num, null);
        Message(0.5, new VitalReadings(HeartRate: 80));

        _recorder.Rebase(Patient, VitalsRecorder.CuffOf(Cuff(160, 100, takenAt: -60)));

        Assert.Empty(Tick(1));

        Message(1.5, Cuff(160, 100, takenAt: -60));
        Assert.Null(Assert.Single(Tick(2)).Cuff);
    }

    [Fact]
    public void Each_patient_has_a_row_of_their_own()
    {
        _recorder.Start(Patient, Num, null);
        _recorder.Start("another", 8, null);
        Message(0.4, new VitalReadings(HeartRate: 80));
        Message(0.6, new VitalReadings(HeartRate: 60), patientId: "another");

        var rows = Tick(1).OrderBy(r => r.PatientNum).ToList();

        Assert.Equal(new[] { Num, 8 }, rows.Select(r => r.PatientNum));
        Assert.Equal(new int?[] { 80, 60 }, rows.Select(r => r.HeartRate));
        Assert.All(rows, r => Assert.Equal(Second(1), r.Timestamp));
    }
}
