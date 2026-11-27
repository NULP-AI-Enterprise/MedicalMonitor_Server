using MedicalMonitorServer.Dal.History;

namespace MedicalMonitorServer.Tests;

/// <summary>
/// The history database: the patients a station admitted and a row per patient per second.
/// A "restart" is a new store over the same file.
/// </summary>
public sealed class HistoryStoreTests : IDisposable
{
    private const string Monitor = "10.10.46.48";

    private static readonly DateTimeOffset Admission = new(2026, 11, 27, 10, 0, 0, TimeSpan.Zero);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "mm-history-" + Guid.NewGuid().ToString("N"));
    private readonly List<SqliteHistoryStore> _open = new();

    private string File => Path.Combine(_folder, "data", "history.db");

    public void Dispose()
    {
        foreach (var store in _open) store.Dispose();
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private SqliteHistoryStore Open()
    {
        var store = new SqliteHistoryStore(File);
        _open.Add(store);
        return store;
    }

    private static NewRecordedPatient New(string id, string? drAlexId = "DA-1001", string monitorIp = Monitor, int minutesLater = 0) =>
        new(id, drAlexId, "Іваненко Іван Іванович", 4, "Adu", "Sepsis", monitorIp, Admission.AddMinutes(minutesLater));

    private static long Second(int seconds) => Admission.AddSeconds(seconds).ToUnixTimeSeconds();

    private static VitalsRow Row(int num, int second, int heartRate, CuffReading? cuff = null, string? extra = null) =>
        new(num, Second(second), heartRate, 98, heartRate, 12, 36.5, cuff, extra);

    // ------------------------------------------------------------------ patients

    [Fact]
    public void An_admitted_patient_is_read_back_by_the_next_store()
    {
        var admitted = Open().Admit(New("uuid-1"));

        var active = Assert.Single(Open().GetActive());

        Assert.Equal(admitted, active);
        Assert.Equal("uuid-1", active.Id);
        Assert.Equal("DA-1001", active.DrAlexId);
        Assert.Equal("Іваненко Іван Іванович", active.FullName);
        Assert.Equal(4, active.BedNumber);
        Assert.Equal("Sepsis", active.Diagnosis);
        Assert.Equal(Monitor, active.MonitorIp);
        Assert.Equal(Admission, active.AdmittedAt);
        Assert.Null(active.DischargedAt);
    }

    [Fact]
    public void Each_patient_gets_the_next_number()
    {
        var store = Open();

        Assert.Equal(1, store.Admit(New("uuid-1")).Num);
        Assert.Equal(2, store.Admit(New("uuid-2", monitorIp: "10.10.46.49")).Num);

        // Across a restart as well, and a discharge frees no number: rows still carry it.
        store.Discharge("uuid-2", Admission.AddHours(1));
        Assert.Equal(3, Open().Admit(New("uuid-3", monitorIp: "10.10.46.49")).Num);
    }

    [Fact]
    public void The_same_DrAlex_id_can_be_admitted_again()
    {
        // It is what somebody typed, not a key: the same person comes back.
        var store = Open();
        store.Admit(New("uuid-1", drAlexId: "DA-7"));
        store.Discharge("uuid-1", Admission.AddHours(1));
        store.Admit(New("uuid-2", drAlexId: "DA-7", minutesLater: 120));

        Assert.Equal(new[] { "uuid-2", "uuid-1" }, store.GetPatients("DA-7", 10).Select(p => p.Id));
        Assert.Empty(store.GetPatients("DA-8", 10));
    }

    [Fact]
    public void A_patient_with_only_a_DrAlex_id_or_only_a_name_is_kept_as_entered()
    {
        var store = Open();
        store.Admit(new NewRecordedPatient("uuid-1", "DA-1", null, 1, "Adu", null, Monitor, Admission));
        store.Admit(new NewRecordedPatient("uuid-2", null, "Петренко", 2, "Ped", null, "10.10.46.49", Admission));

        Assert.Null(store.GetPatient("uuid-1")!.FullName);
        Assert.Null(store.GetPatient("uuid-2")!.DrAlexId);
        Assert.Null(store.GetPatient("nobody"));
    }

    [Fact]
    public void What_a_station_changed_is_there_for_the_next_store()
    {
        var store = Open();
        store.Admit(New("uuid-1"));

        store.Update("uuid-1", "DA-2002", "Іваненко І. І.", 6, "Ped", null);

        var patient = Open().GetPatient("uuid-1")!;
        Assert.Equal("DA-2002", patient.DrAlexId);
        Assert.Equal("Іваненко І. І.", patient.FullName);
        Assert.Equal(6, patient.BedNumber);
        Assert.Equal("Ped", patient.PatientType);
        Assert.Null(patient.Diagnosis);
    }

    [Fact]
    public void A_discharged_patient_keeps_their_rows_and_is_no_longer_active()
    {
        var store = Open();
        var patient = store.Admit(New("uuid-1"));
        store.Append(new[] { Row(patient.Num, 1, 80) });

        Assert.True(store.Discharge("uuid-1", Admission.AddHours(2)));
        Assert.False(store.Discharge("uuid-1", Admission.AddHours(3)));   // once
        Assert.False(store.Discharge("nobody", Admission));

        var next = Open();
        Assert.Empty(next.GetActive());
        var kept = next.GetPatient("uuid-1")!;
        Assert.Equal(Admission.AddHours(2), kept.DischargedAt);
        Assert.Null(kept.MonitorIp);
        Assert.Single(next.GetVitals(kept.Num, Second(0), Second(10), 100));
    }

    [Fact]
    public void A_move_puts_the_patient_on_the_new_monitor_and_whoever_was_there_on_none()
    {
        var store = Open();
        store.Admit(New("uuid-1"));
        store.Admit(New("uuid-2", monitorIp: "10.10.46.49"));

        store.MoveMonitor("uuid-1", "10.10.46.49", displacedId: "uuid-2", standingCuff: null, Admission.AddHours(1));

        Assert.Equal("10.10.46.49", store.GetPatient("uuid-1")!.MonitorIp);
        Assert.Null(store.GetPatient("uuid-2")!.MonitorIp);
    }

    [Fact]
    public void The_cuff_reading_that_stood_on_the_monitor_at_admission_is_kept_with_the_patient()
    {
        // It is not theirs, and the next server must know that too: the monitor goes on showing it.
        var standing = new CuffReading(135, 75, 95, null);
        var admitted = Open().Admit(New("uuid-1") with { StandingCuff = standing });
        Open().Admit(New("uuid-2", monitorIp: "10.10.46.49"));

        var next = Open();
        var patient = next.GetPatient("uuid-1")!;
        Assert.Equal(standing, patient.StandingCuff);
        Assert.Equal(Admission.ToUnixTimeSeconds(), patient.StandingSince);
        Assert.Equal(admitted, next.GetActive()[0]);

        var none = next.GetPatient("uuid-2")!;
        Assert.Null(none.StandingCuff);
        Assert.Equal(Admission.ToUnixTimeSeconds(), none.StandingSince);
    }

    [Fact]
    public void A_move_replaces_the_standing_reading_with_the_new_monitors()
    {
        var store = Open();
        store.Admit(New("uuid-1") with { StandingCuff = new CuffReading(135, 75, 95, null) });

        var onTheOther = new CuffReading(160, 100, 120, Second(-60));
        store.MoveMonitor("uuid-1", "10.10.46.49", null, onTheOther, Admission.AddHours(1));

        var moved = Open().GetPatient("uuid-1")!;
        Assert.Equal(onTheOther, moved.StandingCuff);
        Assert.Equal(Admission.AddHours(1).ToUnixTimeSeconds(), moved.StandingSince);

        // A monitor that shows no reading: then there is none to mistake for theirs.
        store.MoveMonitor("uuid-1", Monitor, null, null, Admission.AddHours(2));
        Assert.Null(store.GetPatient("uuid-1")!.StandingCuff);
        Assert.Equal(Admission.AddHours(2).ToUnixTimeSeconds(), store.GetPatient("uuid-1")!.StandingSince);
    }

    // ------------------------------------------------------------------ coming back

    private static IReadOnlyList<RecordedAdmission> StaysOf(SqliteHistoryStore store, RecordedPatient patient) =>
        store.GetAdmissions(new[] { patient.Num })[patient.Num];

    [Fact]
    public void A_discharged_patient_admitted_again_keeps_their_id_their_number_and_their_rows()
    {
        var store = Open();
        var first = store.Admit(New("uuid-1"));
        store.Append(new[] { Row(first.Num, 1, 80) });
        store.Discharge("uuid-1", Admission.AddHours(1));
        store.Admit(New("uuid-2", monitorIp: "10.10.46.49", minutesLater: 90));

        var back = store.Readmit("uuid-1", "10.10.46.50", bedNumber: 7, Admission.AddHours(2), standingCuff: null)!;

        // The same patient: nobody new was made up, and what is written next joins what was.
        Assert.Equal(first.Num, back.Num);
        Assert.Equal("DA-1001", back.DrAlexId);
        Assert.Equal("Іваненко Іван Іванович", back.FullName);
        Assert.Equal("Sepsis", back.Diagnosis);
        Assert.Equal(7, back.BedNumber);                              // where they lie this time
        Assert.Equal("10.10.46.50", back.MonitorIp);
        Assert.Equal(Admission.AddHours(2), back.AdmittedAt);
        Assert.Null(back.DischargedAt);

        store.Append(new[] { Row(back.Num, 7300, 90) });

        var next = Open();
        Assert.Equal(new[] { "uuid-2", "uuid-1" }, next.GetActive().Select(p => p.Id));
        Assert.Equal(back, next.GetActive()[1]);
        Assert.Equal(new int?[] { 80, 90 }, next.GetVitals(back.Num, 0, long.MaxValue, 100).Select(r => r.HeartRate));
        Assert.Equal(3, next.Admit(New("uuid-3", monitorIp: "10.10.46.51")).Num);
    }

    [Fact]
    public void Every_stay_is_kept_with_its_admission_its_discharge_and_where_the_patient_was()
    {
        var store = Open();
        var patient = store.Admit(New("uuid-1"));
        Assert.Equal(new[] { new RecordedAdmission(Admission, null, 4, Monitor) }, StaysOf(store, patient));

        store.Discharge("uuid-1", Admission.AddHours(1));
        store.Readmit("uuid-1", "10.10.46.49", 7, Admission.AddHours(2), null);

        // During a stay the bed and the monitor are the ones the patient has now ...
        store.Update("uuid-1", "DA-1001", "Іваненко Іван Іванович", 9, "Adu", "Sepsis");
        store.MoveMonitor("uuid-1", "10.10.46.50", null, null, Admission.AddHours(3));
        // ... and a while without a monitor leaves the last one standing.
        store.MoveMonitor("uuid-1", null, null, null, Admission.AddHours(4));
        store.Discharge("uuid-1", Admission.AddHours(5));

        Assert.Equal(
            new[]
            {
                new RecordedAdmission(Admission, Admission.AddHours(1), 4, Monitor),
                new RecordedAdmission(Admission.AddHours(2), Admission.AddHours(5), 9, "10.10.46.50"),
            },
            StaysOf(Open(), patient));

        // The patient's own row says when they came and went last.
        var kept = Open().GetPatient("uuid-1")!;
        Assert.Equal(Admission.AddHours(2), kept.AdmittedAt);
        Assert.Equal(Admission.AddHours(5), kept.DischargedAt);
    }

    [Fact]
    public void The_stays_of_several_patients_are_read_together_and_nobody_unknown_has_any()
    {
        var store = Open();
        var first = store.Admit(New("uuid-1"));
        var second = store.Admit(New("uuid-2", monitorIp: "10.10.46.49", minutesLater: 5));
        store.Discharge("uuid-1", Admission.AddHours(1));
        store.Readmit("uuid-1", Monitor, 4, Admission.AddHours(2), null);

        var stays = store.GetAdmissions(new[] { first.Num, second.Num, 999 });

        Assert.Equal(new[] { first.Num, second.Num }, stays.Keys.OrderBy(num => num));
        Assert.Equal(new[] { Admission, Admission.AddHours(2) }, stays[first.Num].Select(s => s.AdmittedAt));
        Assert.Equal(Admission.AddMinutes(5), Assert.Single(stays[second.Num]).AdmittedAt);
        Assert.Empty(store.GetAdmissions(Array.Empty<int>()));
    }

    [Fact]
    public void Nobody_is_admitted_again_while_they_are_admitted_and_nobody_unknown_is()
    {
        var store = Open();
        var patient = store.Admit(New("uuid-1"));

        Assert.Null(store.Readmit("uuid-1", "10.10.46.49", 7, Admission.AddHours(1), null));
        Assert.Null(store.Readmit("nobody", "10.10.46.49", 7, Admission.AddHours(1), null));

        // Nothing changed: the patient is where they were, in the stay they were in.
        Assert.Equal(patient, Assert.Single(store.GetActive()));
        Assert.Equal(new[] { new RecordedAdmission(Admission, null, 4, Monitor) }, StaysOf(store, patient));
    }

    [Fact]
    public void A_patient_who_left_and_came_back_within_one_second_has_two_stays()
    {
        // Pressed by mistake and put right at once: no key of the table may stand in the way.
        var store = Open();
        var patient = store.Admit(New("uuid-1"));
        store.Discharge("uuid-1", Admission);

        Assert.NotNull(store.Readmit("uuid-1", Monitor, 4, Admission, null));

        Assert.Equal(
            new[] { new RecordedAdmission(Admission, Admission, 4, Monitor), new RecordedAdmission(Admission, null, 4, Monitor) },
            StaysOf(store, patient));
    }

    [Fact]
    public void The_reading_that_stands_on_the_monitor_when_they_come_back_is_kept_with_them_too()
    {
        var store = Open();
        store.Admit(New("uuid-1") with { StandingCuff = new CuffReading(135, 75, 95, null) });
        store.Discharge("uuid-1", Admission.AddHours(1));

        var standing = new CuffReading(121, 79, 93, Second(3500));
        store.Readmit("uuid-1", Monitor, 4, Admission.AddHours(2), standing);

        var back = Open().GetPatient("uuid-1")!;
        Assert.Equal(standing, back.StandingCuff);
        Assert.Equal(Admission.AddHours(2).ToUnixTimeSeconds(), back.StandingSince);

        // And none, when the monitor shows none: the one from their first stay is not theirs to keep.
        store.Discharge("uuid-1", Admission.AddHours(3));
        store.Readmit("uuid-1", Monitor, 4, Admission.AddHours(4), null);
        Assert.Null(store.GetPatient("uuid-1")!.StandingCuff);
    }

    [Fact]
    public void Discharged_patients_are_listed_the_last_discharged_first_without_those_admitted_again()
    {
        var store = Open();
        store.Admit(New("uuid-1", drAlexId: "DA-7"));
        store.Admit(New("uuid-2", drAlexId: "DA-8", monitorIp: "10.10.46.49", minutesLater: 1));
        store.Admit(New("uuid-3", drAlexId: "DA-7", monitorIp: "10.10.46.50", minutesLater: 2));
        store.Admit(New("uuid-4", monitorIp: "10.10.46.51", minutesLater: 3));            // stays admitted
        store.Discharge("uuid-2", Admission.AddHours(1));
        store.Discharge("uuid-1", Admission.AddHours(2));
        store.Discharge("uuid-3", Admission.AddHours(3));

        Assert.Equal(new[] { "uuid-3", "uuid-1", "uuid-2" }, store.GetDischarged(null, 10).Select(p => p.Id));
        Assert.Equal(new[] { "uuid-3", "uuid-1" }, store.GetDischarged(null, 2).Select(p => p.Id));
        Assert.Equal(new[] { "uuid-3", "uuid-1" }, store.GetDischarged("DA-7", 10).Select(p => p.Id));

        // Back on a monitor, they are nobody to admit again; discharged once more, the first to offer.
        store.Readmit("uuid-2", Monitor, 4, Admission.AddHours(4), null);
        Assert.Equal(new[] { "uuid-3", "uuid-1" }, store.GetDischarged(null, 10).Select(p => p.Id));

        store.Discharge("uuid-2", Admission.AddHours(5));
        Assert.Equal(new[] { "uuid-2", "uuid-3", "uuid-1" }, Open().GetDischarged(null, 10).Select(p => p.Id));
    }

    // ------------------------------------------------------------------ rows

    [Fact]
    public void Rows_of_one_second_are_written_together_and_read_back_in_order()
    {
        var store = Open();
        var first = store.Admit(New("uuid-1"));
        var second = store.Admit(New("uuid-2", monitorIp: "10.10.46.49"));

        store.Append(new[] { Row(first.Num, 1, 80), Row(second.Num, 1, 60) });
        store.Append(new[] { Row(first.Num, 2, 81, extra: """{"art.sys":118,"ecg.st.ii":0.05}"""), Row(second.Num, 2, 61) });
        store.Append(new[] { Row(first.Num, 3, 82, cuff: new CuffReading(120, 80, 93, Second(3))) });

        var rows = Open().GetVitals(first.Num, Second(0), Second(10), 100);

        Assert.Equal(new[] { Second(1), Second(2), Second(3) }, rows.Select(r => r.Timestamp));
        Assert.Equal(new int?[] { 80, 81, 82 }, rows.Select(r => r.HeartRate));
        Assert.Equal(36.5, rows[0].Temperature);
        Assert.Null(rows[0].Cuff);
        Assert.Null(rows[0].Extra);
        Assert.Equal("""{"art.sys":118,"ecg.st.ii":0.05}""", rows[1].Extra);
        Assert.Equal(new CuffReading(120, 80, 93, Second(3)), rows[2].Cuff);

        Assert.Equal(new int?[] { 60, 61 }, store.GetVitals(second.Num, Second(0), Second(10), 100).Select(r => r.HeartRate));
    }

    [Fact]
    public void A_value_that_was_not_measured_is_read_back_as_none()
    {
        var store = Open();
        var patient = store.Admit(New("uuid-1"));

        store.Append(new[] { new VitalsRow(patient.Num, Second(1), null, null, null, null, null, null, null) });

        var row = Assert.Single(store.GetVitals(patient.Num, Second(0), Second(10), 100));
        Assert.Equal(new VitalsRow(patient.Num, Second(1), null, null, null, null, null, null, null), row);
    }

    [Fact]
    public void The_newest_rows_of_the_stretch_are_returned_when_there_are_more_than_asked_for()
    {
        var store = Open();
        var patient = store.Admit(New("uuid-1"));
        store.Append(Enumerable.Range(1, 10).Select(s => Row(patient.Num, s, 70 + s)).ToList());

        var rows = store.GetVitals(patient.Num, Second(2), Second(8), limit: 3);

        Assert.Equal(new[] { Second(6), Second(7), Second(8) }, rows.Select(r => r.Timestamp));
    }

    [Fact]
    public void The_list_of_patients_says_when_each_was_recorded_last()
    {
        var store = Open();
        var patient = store.Admit(New("uuid-1"));
        store.Admit(New("uuid-2", monitorIp: "10.10.46.49", minutesLater: 5));
        store.Append(new[] { Row(patient.Num, 1, 80), Row(patient.Num, 42, 80) });

        var listed = store.GetPatients(null, 10);

        Assert.Equal(new[] { "uuid-2", "uuid-1" }, listed.Select(p => p.Id));   // last admitted first
        Assert.Null(listed[0].LastRecordedAt);
        Assert.Equal(Second(42), listed[1].LastRecordedAt);
        Assert.Single(store.GetPatients(null, 1));
    }

    [Fact]
    public void The_cuff_reading_recorded_last_is_found_behind_the_rows_without_one()
    {
        var store = Open();
        var patient = store.Admit(New("uuid-1"));
        Assert.Null(store.GetLastCuff(patient.Num));

        store.Append(new[] { Row(patient.Num, 1, 80, cuff: new CuffReading(120, 80, 93, Second(1))) });
        store.Append(new[] { Row(patient.Num, 2, 80, cuff: new CuffReading(118, 76, 90, null)) });
        store.Append(Enumerable.Range(3, 50).Select(s => Row(patient.Num, s, 80)).ToList());

        Assert.Equal(new RecordedCuff(new CuffReading(118, 76, 90, null), Second(2)), Open().GetLastCuff(patient.Num));
    }

    [Fact]
    public void A_second_written_twice_keeps_what_was_written_last()
    {
        // The clock was set back: no duplicate key may stop the recording.
        var store = Open();
        var patient = store.Admit(New("uuid-1"));

        store.Append(new[] { Row(patient.Num, 1, 80) });
        store.Append(new[] { Row(patient.Num, 1, 95) });

        Assert.Equal(95, Assert.Single(store.GetVitals(patient.Num, Second(0), Second(10), 100)).HeartRate);
    }

    [Fact]
    public void Rows_for_a_patient_who_was_never_admitted_are_refused_together_with_the_rest_of_the_second()
    {
        var store = Open();
        var patient = store.Admit(New("uuid-1"));

        Assert.ThrowsAny<Exception>(() => store.Append(new[] { Row(patient.Num, 1, 80), Row(999, 1, 60) }));

        Assert.Empty(store.GetVitals(patient.Num, Second(0), Second(10), 100));
    }

    // ------------------------------------------------------------------ the file

    [Fact]
    public void Rows_committed_by_a_store_that_was_never_closed_are_there_for_the_next_one()
    {
        // An update stops the server by killing it: nothing gets to close the file.
        var abandoned = new SqliteHistoryStore(File);
        var patient = abandoned.Admit(New("uuid-1"));
        abandoned.Append(new[] { Row(patient.Num, 1, 80), Row(patient.Num, 2, 81) });

        try
        {
            var next = Open();

            Assert.Equal(2, next.GetVitals(patient.Num, Second(0), Second(10), 100).Count);
            Assert.Single(next.GetActive());
        }
        finally
        {
            abandoned.Dispose();   // or the temp folder could not be removed
        }
    }

    [Fact]
    public void The_database_says_how_much_room_it_takes()
    {
        var store = Open();
        var patient = store.Admit(New("uuid-1"));
        var empty = store.SizeBytes;

        store.Append(Enumerable.Range(1, 2000).Select(s => Row(patient.Num, s, 80, extra: """{"art.sys":118,"art.dia":76}""")).ToList());

        Assert.True(empty > 0);
        Assert.True(store.SizeBytes > empty);
    }

    /// <summary>
    /// The tables as an earlier server made them, with a patient who is admitted and one row of
    /// theirs. Schema 1 is the first server with a history; schema 2 also kept the cuff reading
    /// that stood on the monitor at admission, and here has a second patient, discharged since.
    /// </summary>
    private void WriteOldDatabase(int version)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(File)!);
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={File};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();

        var second = version < 2 ? string.Empty : $"""
            ALTER TABLE patients ADD COLUMN standing_nibp_sys INTEGER;
            ALTER TABLE patients ADD COLUMN standing_nibp_dia INTEGER;
            ALTER TABLE patients ADD COLUMN standing_nibp_map INTEGER;
            ALTER TABLE patients ADD COLUMN standing_nibp_at  INTEGER;
            ALTER TABLE patients ADD COLUMN standing_since    INTEGER;
            UPDATE patients SET standing_nibp_sys = 135, standing_nibp_dia = 75, standing_since = admitted_at;
            INSERT INTO patients (id, num, dralex_id, full_name, bed_number, patient_type, admitted_at, discharged_at, standing_since)
                VALUES ('uuid-gone', 2, 'DA-2', 'Петренко', 6, 'Ped', {Second(-7200)}, {Second(-3600)}, {Second(-7200)});
            INSERT INTO vitals (patient_num, ts, hr) VALUES (2, {Second(-5000)}, 64);
            """;

        command.CommandText = $"""
            CREATE TABLE patients (
                id            TEXT PRIMARY KEY,
                num           INTEGER NOT NULL UNIQUE,
                dralex_id     TEXT,
                full_name     TEXT,
                bed_number    INTEGER NOT NULL DEFAULT 0,
                patient_type  TEXT NOT NULL DEFAULT 'Adu',
                diagnosis     TEXT,
                monitor_ip    TEXT,
                admitted_at   INTEGER NOT NULL,
                discharged_at INTEGER
            );
            CREATE INDEX patients_dralex ON patients (dralex_id);
            CREATE TABLE vitals (
                patient_num INTEGER NOT NULL REFERENCES patients (num),
                ts          INTEGER NOT NULL,
                hr INTEGER, spo2 INTEGER, pr INTEGER, rr INTEGER, temp REAL,
                nibp_sys INTEGER, nibp_dia INTEGER, nibp_map INTEGER, nibp_at INTEGER,
                extra TEXT,
                PRIMARY KEY (patient_num, ts)
            ) WITHOUT ROWID;
            INSERT INTO patients (id, num, dralex_id, full_name, bed_number, patient_type, monitor_ip, admitted_at)
                VALUES ('uuid-old', 1, 'DA-1', 'Іваненко', 4, 'Adu', '{Monitor}', {Admission.ToUnixTimeSeconds()});
            INSERT INTO vitals (patient_num, ts, hr, nibp_sys, nibp_dia, nibp_map) VALUES (1, {Second(5)}, 80, 120, 80, 93);
            {second}
            PRAGMA user_version = {version};
            """;
        command.ExecuteNonQuery();
    }

    [Fact]
    public void A_database_of_an_older_server_is_brought_up_to_date_with_everything_in_it()
    {
        WriteOldDatabase(version: 1);

        var store = Open();

        var patient = Assert.Single(store.GetActive());
        Assert.Equal("uuid-old", patient.Id);
        Assert.Equal("DA-1", patient.DrAlexId);
        Assert.Equal(Admission, patient.AdmittedAt);
        Assert.Null(patient.StandingCuff);          // that server did not keep it
        Assert.Null(patient.StandingSince);
        Assert.Equal(new RecordedCuff(new CuffReading(120, 80, 93, null), Second(5)), store.GetLastCuff(patient.Num));

        // The stay they are in is there as if today's server had admitted them.
        Assert.Equal(new[] { new RecordedAdmission(Admission, null, 4, Monitor) }, StaysOf(store, patient));

        // And it works as a database of today: the next patient, with what today's server keeps.
        var next = store.Admit(New("uuid-new", monitorIp: "10.10.46.49") with { StandingCuff = new CuffReading(135, 75, 95, null) });
        Assert.Equal(2, next.Num);
        Assert.NotNull(Open().GetPatient("uuid-new")!.StandingCuff);
        Assert.Null(store.PutAside);
    }

    [Fact]
    public void A_database_from_before_patients_could_come_back_gets_a_stay_for_everybody_in_it()
    {
        WriteOldDatabase(version: 2);

        var store = Open();

        // The patient on a monitor is in a stay that goes on, with what was kept about them ...
        var admitted = Assert.Single(store.GetActive());
        Assert.Equal("uuid-old", admitted.Id);
        Assert.Equal(new CuffReading(135, 75, null, null), admitted.StandingCuff);
        Assert.Equal(new[] { new RecordedAdmission(Admission, null, 4, Monitor) }, StaysOf(store, admitted));

        // ... and the one who had left has the stay that ended. Its monitor was not kept.
        var gone = Assert.Single(store.GetDischarged(null, 10));
        Assert.Equal("uuid-gone", gone.Id);
        Assert.Equal(
            new[] { new RecordedAdmission(Admission.AddHours(-2), Admission.AddHours(-1), 6, null) },
            StaysOf(store, gone));

        // Who can now come back, into the history they have.
        var back = store.Readmit("uuid-gone", "10.10.46.49", 8, Admission.AddHours(1), null)!;
        store.Append(new[] { Row(back.Num, 3700, 70) });

        var next = Open();
        Assert.Equal(new int?[] { 64, 70 }, next.GetVitals(back.Num, 0, long.MaxValue, 100).Select(r => r.HeartRate));
        Assert.Equal(2, StaysOf(next, back).Count);
        Assert.Null(next.PutAside);
    }

    [Fact]
    public void A_database_from_a_newer_server_is_not_touched()
    {
        Open().Dispose();
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={File};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA user_version = {SqliteHistoryStore.SchemaVersion + 1};";
            command.ExecuteNonQuery();
        }

        var error = Assert.Throws<HistorySchemaException>(() => new SqliteHistoryStore(File));

        Assert.Contains("newer server", error.Message);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(File)!, "*unreadable*"));
    }

    [Fact]
    public void A_file_that_is_not_a_database_is_put_aside_and_a_new_one_is_started()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(File)!);
        System.IO.File.WriteAllText(File, new string('x', 5000));

        var store = Open();

        Assert.NotNull(store.PutAside);
        Assert.Equal(new string('x', 5000), System.IO.File.ReadAllText(store.PutAside!));
        Assert.Equal(1, store.Admit(New("uuid-1")).Num);
        Assert.Null(Open().PutAside);
    }
}
