using MedicalMonitorServer.Contracts.Api;
using MedicalMonitorServer.Contracts.Models;
using MedicalMonitorServer.Core.Monitoring;
using MedicalMonitorServer.Dal.History;
using MedicalMonitorServer.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace MedicalMonitorServer.Tests;

/// <summary>
/// A patient of the station's: admitted at a station under an id made up here, recorded
/// once a second until a station discharges them, and held on their monitor in between
/// whatever the monitor says about its own patient. The real router, pipeline, registry,
/// bindings, recorder and an SQLite file in a temp folder; a "restart" is all of them
/// built again over the same file.
/// </summary>
public sealed class RecordingLifecycleTests : IDisposable
{
    private const string Monitor = "10.10.46.48";
    private const string OtherMonitor = "10.10.46.49";

    // The test's own time for what monitors send and for the once-a-second writes. Later than
    // any real "now" on purpose: an admission is stamped by the real clock, and what is recorded
    // for a patient comes after their admission, never before it.
    private static readonly DateTimeOffset Clock = new(2040, 1, 1, 10, 0, 0, TimeSpan.Zero);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "mm-recording-" + Guid.NewGuid().ToString("N"));
    private readonly List<Server> _servers = new();

    private string HistoryFile => Path.Combine(_folder, "data", "history.db");

    private string RosterFile => Path.Combine(_folder, "data", "patients.json");

    public void Dispose()
    {
        foreach (var server in _servers) server.History.Dispose();
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private sealed class Server
    {
        public FakeHubContext Hub { get; } = new();
        public PatientRegistry Patients { get; } = new();
        public AlertStateService Alerts { get; } = new();
        public MonitorBindings Bindings { get; } = new();
        public TestClock Time { get; } = new(Clock);
        public VitalsRecorder Recorder { get; }
        public FlakyHistoryStore? Store { get; }
        public PatientHistory History { get; }
        public MonitorPipeline Pipeline { get; }
        public MonitorRouter Router { get; }

        public Server(IHistoryStore? store, IPatientRecordStore? records = null)
        {
            Recorder = new VitalsRecorder(Time);
            Store = store is null ? null : new FlakyHistoryStore(store);
            History = new PatientHistory(Store, Recorder, NullLogger<PatientHistory>.Instance);
            Pipeline = new MonitorPipeline(
                Hub, Patients, Alerts, Bindings, new FeedStatus(), NullLogger<MonitorPipeline>.Instance, records, Recorder);
            Router = new MonitorRouter(
                Bindings, Pipeline, Patients, NullLogger<MonitorRouter>.Instance, records: records, history: History);
        }

        /// <summary>The monitor's patient-info message; <paramref name="reportedId"/> empty means "I have no patient id".</summary>
        public Task<string?> MessageAsync(string monitorIp, string reportedId) =>
            Router.ResolveAsync(
                monitorIp,
                new PatientInfo { PidPresent = true, PatientId = reportedId },
                hasReadings: true,
                CancellationToken.None);

        /// <summary>A report of values, with no patient identification in it, as most are.</summary>
        public async Task<string?> ValuesAsync(string monitorIp, VitalReadings readings)
        {
            var patientId = await Router.ResolveAsync(monitorIp, null, hasReadings: true, CancellationToken.None);
            if (patientId is not null)
            {
                await Pipeline.IngestReadingsAsync(patientId, readings, null, DateTimeOffset.UtcNow, CancellationToken.None, complete: true);
            }

            return patientId;
        }

        public Task<AdmitOutcome> AdmitAsync(string patientId, string? drAlexId = "DA-1001", string name = "Іваненко Іван", int bed = 4) =>
            Router.AdmitAsync(patientId, new PatientEntry(drAlexId, name, bed, "Adu", "Sepsis"), CancellationToken.None);

        /// <summary>
        /// The once-a-second write, so many seconds after the start of the test. The clock moves
        /// with it: what a monitor sends next comes in at that time.
        /// </summary>
        public void Tick(int seconds)
        {
            Time.Now = At(seconds);
            History.Flush(At(seconds));
        }

        public PatientInfoDto OnlyPatient => Assert.Single(Patients.GetAll());

        public IReadOnlyList<VitalsRow> Rows(string patientId)
        {
            var patient = Store!.GetPatient(patientId)!;
            return Store.GetVitals(patient.Num, 0, long.MaxValue, 100_000);
        }
    }

    private Server Start(IPatientRecordStore? records = null)
    {
        var server = new Server(new SqliteHistoryStore(HistoryFile), records);
        _servers.Add(server);
        return server;
    }

    /// <summary>Stops a server the way an update does - nothing is flushed or said - and starts the next over the same file.</summary>
    private async Task<Server> RestartAsync(Server running)
    {
        running.History.Dispose();
        var next = Start();
        await next.Router.RestoreAsync(CancellationToken.None);
        return next;
    }

    private static DateTimeOffset At(int seconds) => Clock.AddSeconds(seconds);

    private static VitalReadings Values(int heartRate, int? systolic = null, int? diastolic = null, int? cuffTakenAt = null) => new(
        HeartRate: heartRate,
        SpO2: 98,
        Systolic: systolic,
        Diastolic: diastolic,
        NibpMeasuredAt: cuffTakenAt is { } seconds ? At(seconds) : null);

    // ------------------------------------------------------------------ admission

    [Fact]
    public async Task Admitting_replaces_the_monitors_entry_with_a_patient_under_an_id_made_up_here()
    {
        var server = Start();
        await server.MessageAsync(Monitor, "321");
        Assert.Equal("321", server.OnlyPatient.PatientId);

        var outcome = await server.AdmitAsync("321");

        Assert.Equal(AdmitOutcomeKind.Admitted, outcome.Kind);
        var patient = server.OnlyPatient;
        Assert.Equal(outcome.Patient, patient);
        Assert.True(Guid.TryParse(patient.PatientId, out _));
        Assert.Equal("DA-1001", patient.DrAlexId);
        Assert.Equal("Іваненко Іван", patient.PatientName);
        Assert.Equal(4, patient.BedNumber);
        Assert.Equal("Sepsis", patient.Diagnosis);
        Assert.NotNull(patient.AdmittedAt);

        // The stations see the old entry go and the patient come.
        Assert.Equal(new[] { "321" }, server.Hub.SentTo<string>(HubMethods.PatientRemoved));
        Assert.Equal(patient, server.Hub.SentTo<PatientInfoDto>(HubMethods.PatientAdded).Last());

        // The monitor feeds them now, and the database has them on it.
        Assert.Equal(patient.PatientId, server.Bindings.Get(Monitor)!.PatientId);
        var recorded = Assert.Single(server.Store!.GetActive());
        Assert.Equal(patient.PatientId, recorded.Id);
        Assert.Equal(Monitor, recorded.MonitorIp);
        Assert.Equal(patient.AdmittedAt, recorded.AdmittedAt);
    }

    [Fact]
    public async Task Somebody_on_a_monitor_that_names_nobody_is_admitted_the_same_way()
    {
        var server = Start();
        var anonymous = (await server.ValuesAsync(Monitor, Values(80)))!;
        Assert.True(AnonymousPatient.IsAnonymous(anonymous));

        var outcome = await server.AdmitAsync(anonymous, drAlexId: null, name: "Петренко");

        Assert.Equal(AdmitOutcomeKind.Admitted, outcome.Kind);
        Assert.Null(server.OnlyPatient.DrAlexId);
        Assert.Equal("Петренко", server.OnlyPatient.PatientName);

        // What the monitor measures right now is theirs to show: no blank bed in between.
        Assert.Equal(80, server.Patients.GetReadings(outcome.Patient!.PatientId)!.HeartRate);
    }

    [Fact]
    public async Task Nobody_is_admitted_twice_or_without_a_monitor()
    {
        var server = Start();
        await server.MessageAsync(Monitor, "321");
        var admitted = (await server.AdmitAsync("321")).Patient!;

        Assert.Equal(AdmitOutcomeKind.AlreadyAdmitted, (await server.AdmitAsync(admitted.PatientId)).Kind);
        Assert.Equal(AdmitOutcomeKind.PatientNotFound, (await server.AdmitAsync("321")).Kind);

        // Somebody a station added by hand is on no monitor: there is nothing to record.
        await server.Pipeline.UpsertPatientAsync(new PatientInfoDto("WAITING", "Doe", 9, null), CancellationToken.None);
        Assert.Equal(AdmitOutcomeKind.NoMonitor, (await server.AdmitAsync("WAITING")).Kind);

        Assert.Single(server.Store!.GetActive());
    }

    [Fact]
    public async Task Nobody_is_admitted_when_the_database_cannot_be_written()
    {
        var server = Start();
        await server.MessageAsync(Monitor, "321");
        server.Store!.FailAdmit = true;

        var outcome = await server.AdmitAsync("321");

        // An admission that is not written down is not one: the bed stays as it was.
        Assert.Equal(AdmitOutcomeKind.HistoryUnavailable, outcome.Kind);
        Assert.Equal("321", server.OnlyPatient.PatientId);
        Assert.Null(server.OnlyPatient.AdmittedAt);
        Assert.Equal("321", server.Bindings.Get(Monitor)!.PatientId);
        Assert.Empty(server.Store.GetActive());
    }

    [Fact]
    public async Task Without_a_history_database_nobody_is_admitted()
    {
        var server = new Server(store: null);
        _servers.Add(server);
        await server.MessageAsync(Monitor, "321");

        Assert.Equal(AdmitOutcomeKind.HistoryUnavailable, (await server.AdmitAsync("321")).Kind);
        Assert.Equal("321", server.OnlyPatient.PatientId);
    }

    // ------------------------------------------------------------------ recording

    [Fact]
    public async Task Values_are_recorded_only_between_admission_and_discharge()
    {
        var server = Start();
        await server.MessageAsync(Monitor, "321");

        // Shown, not recorded: nobody was admitted yet.
        await server.ValuesAsync(Monitor, Values(70));
        server.Tick(1);
        Assert.Equal(0, server.Store!.Appends);

        var patient = (await server.AdmitAsync("321")).Patient!;

        await server.ValuesAsync(Monitor, Values(80));
        server.Tick(2);
        await server.ValuesAsync(Monitor, Values(81));
        await server.ValuesAsync(Monitor, Values(82));      // the second's last word
        server.Tick(3);
        server.Tick(4);                                     // seconds the monitor skipped: what it said last
        server.Tick(5);
        server.Tick(6);                                     // silent for longer than that: nothing
        server.Tick(7);

        Assert.Equal(DischargeOutcome.Discharged, await server.Router.DischargeAsync(patient.PatientId, CancellationToken.None));

        // The monitor keeps sending: somebody not admitted, shown and not recorded.
        await server.ValuesAsync(Monitor, Values(90));
        server.Tick(8);

        var rows = server.Rows(patient.PatientId);
        Assert.Equal(new[] { 2, 3, 4, 5 }.Select(s => At(s).ToUnixTimeSeconds()), rows.Select(r => r.Timestamp));
        Assert.Equal(new int?[] { 80, 82, 82, 82 }, rows.Select(r => r.HeartRate));
        Assert.All(rows, r => Assert.Equal(98, r.SpO2));
    }

    [Fact]
    public async Task The_cuff_reading_standing_at_admission_is_not_theirs_and_the_next_one_is_recorded_once()
    {
        var server = Start();
        await server.MessageAsync(Monitor, "321");
        await server.ValuesAsync(Monitor, Values(70, systolic: 150, diastolic: 95, cuffTakenAt: -600));
        var patient = (await server.AdmitAsync("321")).Patient!;

        await server.ValuesAsync(Monitor, Values(80, systolic: 150, diastolic: 95, cuffTakenAt: -600));
        server.Tick(1);
        await server.ValuesAsync(Monitor, Values(80, systolic: 121, diastolic: 79, cuffTakenAt: 2));
        server.Tick(2);
        await server.ValuesAsync(Monitor, Values(81, systolic: 121, diastolic: 79, cuffTakenAt: 2));
        server.Tick(3);

        var rows = server.Rows(patient.PatientId);
        Assert.Equal(3, rows.Count);
        Assert.Null(rows[0].Cuff);
        Assert.Equal(new CuffReading(121, 79, null, At(2).ToUnixTimeSeconds()), rows[1].Cuff);
        Assert.Null(rows[2].Cuff);

        // Nor is it written a second time after a restart, with the monitor still showing it.
        var next = await RestartAsync(server);
        await next.ValuesAsync(Monitor, Values(82, systolic: 121, diastolic: 79, cuffTakenAt: 2));
        next.Tick(10);

        var last = next.Rows(patient.PatientId).Last();
        Assert.Equal(82, last.HeartRate);
        Assert.Null(last.Cuff);
    }

    [Fact]
    public async Task The_reading_that_stood_at_admission_is_not_recorded_after_a_restart_either()
    {
        // Seen on a BSM-3562: admitted with 135/75 on the monitor, no measurement of their own
        // yet, the server restarted - and the old 135/75 went into the patient's history.
        var before = Start();
        await before.MessageAsync(Monitor, "321");
        await before.ValuesAsync(Monitor, Values(70, systolic: 135, diastolic: 75, cuffTakenAt: -600));
        var patient = (await before.AdmitAsync("321")).Patient!;
        await before.ValuesAsync(Monitor, Values(80, systolic: 135, diastolic: 75, cuffTakenAt: -600));
        before.Tick(1);

        var after = await RestartAsync(before);

        // The monitor's stream starts anew: the same reading, and it no longer says when it was taken.
        await after.ValuesAsync(Monitor, Values(81, systolic: 135, diastolic: 75));
        after.Tick(2);
        await after.ValuesAsync(Monitor, Values(82, systolic: 129, diastolic: 88, cuffTakenAt: 60));
        after.Tick(3);

        var rows = after.Rows(patient.PatientId);
        Assert.Equal(new int?[] { 80, 81, 82 }, rows.Select(r => r.HeartRate));
        Assert.Null(rows[0].Cuff);
        Assert.Null(rows[1].Cuff);
        Assert.Equal(new CuffReading(129, 88, null, At(60).ToUnixTimeSeconds()), rows[2].Cuff);
    }

    [Fact]
    public async Task A_reading_whose_time_the_monitor_puts_right_is_recorded_once()
    {
        // Seen on a BSM-3562 too: 129/88 "taken at :54", and three seconds later "taken at :57".
        var server = Start();
        await server.MessageAsync(Monitor, "321");
        var patient = (await server.AdmitAsync("321")).Patient!;

        await server.ValuesAsync(Monitor, Values(80, systolic: 129, diastolic: 88, cuffTakenAt: 54));
        server.Tick(55);
        await server.ValuesAsync(Monitor, Values(80, systolic: 129, diastolic: 88, cuffTakenAt: 57));
        server.Tick(58);

        var rows = server.Rows(patient.PatientId);
        Assert.Equal(2, rows.Count);
        Assert.Equal(new CuffReading(129, 88, null, At(54).ToUnixTimeSeconds()), rows[0].Cuff);
        Assert.Null(rows[1].Cuff);
    }

    [Fact]
    public async Task A_database_that_stops_taking_rows_is_said_to_fail_and_monitoring_goes_on()
    {
        var server = Start();
        await server.MessageAsync(Monitor, "321");
        var patient = (await server.AdmitAsync("321")).Patient!;
        Assert.Equal(PatientHistory.Recording, server.History.Status);

        server.Store!.FailAppend = true;
        for (var second = 1; second <= 3; second++)
        {
            await server.ValuesAsync(Monitor, Values(80 + second));
            server.Tick(second);   // must not throw
        }

        Assert.Equal(PatientHistory.Failing, server.History.Status);
        Assert.Equal(83, server.Patients.GetReadings(patient.PatientId)!.HeartRate);   // the bed is live all the same

        server.Store.FailAppend = false;
        await server.ValuesAsync(Monitor, Values(90));
        server.Tick(4);

        Assert.Equal(PatientHistory.Recording, server.History.Status);
        Assert.Equal(90, Assert.Single(server.Rows(patient.PatientId)).HeartRate);
    }

    [Fact]
    public void The_history_says_whether_it_is_off_failing_or_recording()
    {
        var recorder = new VitalsRecorder();
        var logger = NullLogger<PatientHistory>.Instance;

        using var off = PatientHistory.Open(" ", _folder, recorder, logger);
        Assert.Equal(PatientHistory.Off, off.Status);
        Assert.False(off.IsOn);

        using var recording = PatientHistory.Open(Path.Combine("data", "history.db"), _folder, recorder, logger);
        Assert.Equal(PatientHistory.Recording, recording.Status);
        Assert.True(File.Exists(HistoryFile));

        // A folder where the file should be: it cannot be opened, and the server runs without.
        Directory.CreateDirectory(Path.Combine(_folder, "blocked.db"));
        using var failing = PatientHistory.Open("blocked.db", _folder, recorder, logger);
        Assert.Equal(PatientHistory.Failing, failing.Status);
        Assert.False(failing.IsOn);
    }

    // ------------------------------------------------------------------ the monitor's own patient

    [Fact]
    public async Task An_admitted_patient_stays_when_the_monitor_names_somebody_else_and_the_stations_are_told()
    {
        var server = Start();
        await server.MessageAsync(Monitor, "321");
        var patient = (await server.AdmitAsync("321")).Patient!;

        // Somebody was admitted on the monitor itself; nobody pressed DISCHARGE here.
        Assert.Equal(patient.PatientId, await server.MessageAsync(Monitor, "555"));

        Assert.Equal(patient, server.OnlyPatient);
        var alert = Assert.Single(server.Alerts.GetActiveAlerts());
        Assert.Equal(AlertDto.BuildMonitorPatientChangedId(patient.PatientId), alert.AlertId);
        Assert.Equal(AlertSeverity.Warning, alert.Severity);
        Assert.Equal(DeviceAlarmLevel.Low, alert.Level);
        Assert.Contains("555", alert.Message);
        Assert.Single(server.Hub.SentTo<AlertDto>(HubMethods.AlertTriggered));

        // The recording goes on under the admitted patient ...
        await server.ValuesAsync(Monitor, Values(80));
        server.Tick(1);
        Assert.Single(server.Rows(patient.PatientId));

        // ... and neither the threshold check nor the monitor's own alarm lists take the
        // notice down: it is for a person to answer.
        await server.Pipeline.IngestDeviceAlarmsAsync(patient.PatientId, AlertCategory.Technical, Array.Empty<AlertDto>());
        await server.Pipeline.IngestDeviceAlarmsAsync(patient.PatientId, AlertCategory.Physiological, Array.Empty<AlertDto>());
        Assert.Single(server.Alerts.GetActiveAlerts());

        await server.Router.DischargeAsync(patient.PatientId, CancellationToken.None);
        Assert.Empty(server.Alerts.GetActiveAlerts());
    }

    [Fact]
    public async Task A_monitor_that_reports_its_patient_discharged_is_told_about_too()
    {
        var server = Start();
        await server.MessageAsync(Monitor, "321");
        var patient = (await server.AdmitAsync("321")).Patient!;

        Assert.Equal(patient.PatientId, await server.MessageAsync(Monitor, string.Empty));

        Assert.Equal(patient, server.OnlyPatient);
        Assert.Contains("discharged", Assert.Single(server.Alerts.GetActiveAlerts()).Message);
    }

    [Fact]
    public async Task A_monitor_that_is_given_a_name_for_the_patient_it_had_raises_nothing()
    {
        var server = Start();
        var anonymous = (await server.ValuesAsync(Monitor, Values(80)))!;
        var patient = (await server.AdmitAsync(anonymous)).Patient!;

        // The nurse types the patient's id on the monitor after admitting them here.
        Assert.Equal(patient.PatientId, await server.MessageAsync(Monitor, "321"));

        Assert.Equal(patient, server.OnlyPatient);
        Assert.Empty(server.Alerts.GetActiveAlerts());
    }

    [Fact]
    public async Task What_the_monitor_says_about_its_own_patient_does_not_overwrite_what_the_station_entered()
    {
        var server = Start();
        await server.MessageAsync(Monitor, "321");
        var patient = (await server.AdmitAsync("321")).Patient!;
        var saidBefore = server.Hub.Sent.Count;

        // A Nihon Kohden over LS-NET sends its record of the patient with every report.
        await server.Router.ResolveAsync(
            Monitor,
            new PatientInfo
            {
                PidPresent = true,
                PatientId = "321",
                LastName = "Somebody",
                FirstName = "Else",
                BedNumber = "9",
                PatientType = PatientType.Pediatric,
            },
            hasReadings: true,
            CancellationToken.None);

        Assert.Equal(patient, server.OnlyPatient);
        Assert.Equal(saidBefore, server.Hub.Sent.Count);   // nothing for the stations to redraw
    }

    [Fact]
    public async Task A_gateway_frame_does_not_rename_an_admitted_patient()
    {
        var server = Start();
        await server.MessageAsync(Monitor, "321");
        var patient = (await server.AdmitAsync("321")).Patient!;

        await server.Pipeline.IngestTelemetryAsync(new MonitorUpdateDto(
            patient.PatientId, "Somebody Else", 9, new VitalsDto(64, 97, null, null, null, null), null, DateTimeOffset.UtcNow));

        Assert.Equal(patient, server.OnlyPatient);
        Assert.Equal(64, server.Patients.GetReadings(patient.PatientId)!.HeartRate);
    }

    // ------------------------------------------------------------------ discharge

    [Fact]
    public async Task Discharging_ends_the_recording_and_the_monitor_shows_up_as_somebody_not_admitted()
    {
        var server = Start();
        await server.MessageAsync(Monitor, "321");
        var patient = (await server.AdmitAsync("321")).Patient!;

        Assert.Equal(DischargeOutcome.Discharged, await server.Router.DischargeAsync(patient.PatientId, CancellationToken.None));

        Assert.Empty(server.Patients.GetAll());
        Assert.Empty(server.Store!.GetActive());
        Assert.NotNull(server.Store.GetPatient(patient.PatientId)!.DischargedAt);
        Assert.Contains(patient.PatientId, server.Hub.SentTo<string>(HubMethods.PatientRemoved));

        // The monitor is still on the next patient, or on this one for a minute more.
        await server.MessageAsync(Monitor, "321");

        var shown = server.OnlyPatient;
        Assert.Equal("321", shown.PatientId);
        Assert.Null(shown.AdmittedAt);
        Assert.Null(shown.DrAlexId);

        // And the next one, admitted as somebody new, is a new patient with a history of their own.
        var next = (await server.AdmitAsync("321", drAlexId: "DA-1002")).Patient!;
        Assert.NotEqual(patient.PatientId, next.PatientId);
        Assert.Equal(2, server.Store.GetPatient(next.PatientId)!.Num);
    }

    [Fact]
    public async Task A_patient_is_not_discharged_when_the_database_cannot_be_written()
    {
        var server = Start();
        await server.MessageAsync(Monitor, "321");
        var patient = (await server.AdmitAsync("321")).Patient!;
        server.Store!.FailDischarge = true;

        var outcome = await server.Router.DischargeAsync(patient.PatientId, CancellationToken.None);

        // Taken off the roster but still admitted in the file, they would be put back after a
        // restart on whoever is on the monitor by then.
        Assert.Equal(DischargeOutcome.HistoryUnavailable, outcome);
        Assert.Equal(patient, server.OnlyPatient);
        Assert.Equal(patient.PatientId, await server.MessageAsync(Monitor, "555"));
    }

    [Fact]
    public async Task Somebody_not_admitted_is_only_taken_off_the_roster_and_nobody_unknown_is_discharged()
    {
        var server = Start();
        await server.MessageAsync(Monitor, "321");

        Assert.Equal(DischargeOutcome.Discharged, await server.Router.DischargeAsync("321", CancellationToken.None));
        Assert.Empty(server.Patients.GetAll());
        Assert.Equal(DischargeOutcome.PatientNotFound, await server.Router.DischargeAsync("321", CancellationToken.None));
    }

    // ------------------------------------------------------------------ coming back

    /// <summary>Admitted on <see cref="Monitor"/>, recorded for one second (heart rate 80), and discharged.</summary>
    private static async Task<PatientInfoDto> AdmittedAndDischargedAsync(Server server)
    {
        await server.MessageAsync(Monitor, "321");
        var patient = (await server.AdmitAsync("321")).Patient!;
        await server.ValuesAsync(Monitor, Values(80));
        server.Tick(1);
        Assert.Equal(DischargeOutcome.Discharged, await server.Router.DischargeAsync(patient.PatientId, CancellationToken.None));
        return patient;
    }

    [Fact]
    public async Task A_discharged_patient_admitted_again_comes_back_under_their_id_and_is_recorded_into_the_same_history()
    {
        var server = Start();
        var patient = await AdmittedAndDischargedAsync(server);

        // The monitor is on somebody again - this very patient, back from the operating room.
        await server.MessageAsync(Monitor, "654");
        Assert.Equal("654", server.OnlyPatient.PatientId);

        var outcome = await server.Router.ReadmitAsync("654", patient.PatientId, bedNumber: 7, CancellationToken.None);

        Assert.Equal(AdmitOutcomeKind.Admitted, outcome.Kind);
        var back = server.OnlyPatient;
        Assert.Equal(outcome.Patient, back);
        Assert.Equal(patient.PatientId, back.PatientId);       // nobody new was made up
        Assert.Equal("DA-1001", back.DrAlexId);                  // who they are is what was entered then
        Assert.Equal("Іваненко Іван", back.PatientName);
        Assert.Equal("Sepsis", back.Diagnosis);
        Assert.Equal(7, back.BedNumber);                         // where they lie is what was entered now
        Assert.NotNull(back.AdmittedAt);

        // The stations see the monitor's entry go and the patient come, as at any admission.
        Assert.Equal("654", server.Hub.SentTo<string>(HubMethods.PatientRemoved).Last());
        Assert.Equal(back, server.Hub.SentTo<PatientInfoDto>(HubMethods.PatientAdded).Last());
        Assert.Equal(patient.PatientId, server.Bindings.Get(Monitor)!.PatientId);

        // What the monitor measures goes on where their history stopped.
        await server.ValuesAsync(Monitor, Values(90));
        server.Tick(600);

        var rows = server.Rows(patient.PatientId);
        Assert.Equal(new[] { 1, 600 }.Select(s => At(s).ToUnixTimeSeconds()), rows.Select(r => r.Timestamp));
        Assert.Equal(new int?[] { 80, 90 }, rows.Select(r => r.HeartRate));

        // One patient in the database, in their second stay.
        var recorded = Assert.Single(server.Store!.GetPatients(null, 10));
        Assert.Equal(recorded.Id, Assert.Single(server.Store.GetActive()).Id);
        var stays = server.Store.GetAdmissions(new[] { recorded.Num })[recorded.Num];
        Assert.Equal(2, stays.Count);
        Assert.NotNull(stays[0].DischargedAt);
        Assert.Equal(new RecordedAdmission(back.AdmittedAt!.Value, null, 7, Monitor), stays[1]);
    }

    [Fact]
    public async Task A_patient_who_came_back_is_held_on_the_monitor_and_discharged_like_anybody_admitted()
    {
        var server = Start();
        var patient = await AdmittedAndDischargedAsync(server);
        await server.MessageAsync(Monitor, "321");
        await server.Router.ReadmitAsync("321", patient.PatientId, 4, CancellationToken.None);

        // What the monitor calls its patient changes nothing, and is said to the stations.
        Assert.Equal(patient.PatientId, await server.MessageAsync(Monitor, "555"));
        Assert.Equal(patient.PatientId, server.OnlyPatient.PatientId);
        Assert.Contains("555", Assert.Single(server.Alerts.GetActiveAlerts()).Message);

        Assert.Equal(DischargeOutcome.Discharged, await server.Router.DischargeAsync(patient.PatientId, CancellationToken.None));

        Assert.Empty(server.Patients.GetAll());
        Assert.Empty(server.Alerts.GetActiveAlerts());
        Assert.Empty(server.Store!.GetActive());
        Assert.Equal(patient.PatientId, Assert.Single(server.Store.GetDischarged(null, 10)).Id);

        // Nothing of theirs is recorded after it.
        await server.ValuesAsync(Monitor, Values(95));
        server.Tick(700);
        Assert.Single(server.Rows(patient.PatientId));
    }

    [Fact]
    public async Task The_cuff_reading_standing_when_they_come_back_is_not_recorded_and_the_next_one_is()
    {
        var server = Start();
        await server.MessageAsync(Monitor, "321");
        var patient = (await server.AdmitAsync("321")).Patient!;
        await server.ValuesAsync(Monitor, Values(80, systolic: 121, diastolic: 79, cuffTakenAt: 1));
        server.Tick(2);
        await server.Router.DischargeAsync(patient.PatientId, CancellationToken.None);

        // Away for ten minutes. The monitor measured in between - whoever was on it, nobody was
        // admitted - and that reading is what stands on it when the patient is admitted again.
        await server.ValuesAsync(Monitor, Values(70, systolic: 150, diastolic: 95, cuffTakenAt: 300));
        var standIn = server.OnlyPatient.PatientId;
        await server.Router.ReadmitAsync(standIn, patient.PatientId, 4, CancellationToken.None);

        await server.ValuesAsync(Monitor, Values(84, systolic: 150, diastolic: 95, cuffTakenAt: 300));
        server.Tick(600);
        await server.ValuesAsync(Monitor, Values(85, systolic: 118, diastolic: 76, cuffTakenAt: 660));
        server.Tick(661);

        var rows = server.Rows(patient.PatientId);
        Assert.Equal(new int?[] { 80, 84, 85 }, rows.Select(r => r.HeartRate));
        Assert.Equal(new CuffReading(121, 79, null, At(1).ToUnixTimeSeconds()), rows[0].Cuff);
        Assert.Null(rows[1].Cuff);
        Assert.Equal(new CuffReading(118, 76, null, At(660).ToUnixTimeSeconds()), rows[2].Cuff);
    }

    [Fact]
    public void After_a_restart_the_reading_that_stood_when_they_came_back_is_still_not_theirs()
    {
        // The times are set by hand here: what decides is whether the patient came back after
        // their last recorded reading, and an admission through the router is stamped by the
        // real clock, which the recorded rows of these tests are not on.
        var cameBackWith = new CuffReading(150, 95, null, At(300).ToUnixTimeSeconds());
        int num;
        using (var before = new SqliteHistoryStore(HistoryFile))
        {
            num = before.Admit(new NewRecordedPatient("uuid-1", "DA-1001", null, 4, "Adu", null, Monitor, At(0))).Num;
            before.Append(new[]
            {
                new VitalsRow(num, At(2).ToUnixTimeSeconds(), 80, 98, null, null, null, new CuffReading(121, 79, null, At(1).ToUnixTimeSeconds()), null),
            });
            before.Discharge("uuid-1", At(10));
            before.Readmit("uuid-1", Monitor, 4, At(600), cameBackWith);
        }

        var server = Start();
        Assert.Equal("uuid-1", Assert.Single(server.History.Resume()).Id);

        // The monitor still shows it, and after the restart no longer says when it was taken.
        server.Recorder.Observe("uuid-1", Values(84, systolic: 150, diastolic: 95));
        server.Tick(601);
        server.Recorder.Observe("uuid-1", Values(85, systolic: 118, diastolic: 76, cuffTakenAt: 660));
        server.Tick(661);

        var rows = server.Store!.GetVitals(num, 0, long.MaxValue, 100);
        Assert.Equal(new int?[] { 80, 84, 85 }, rows.Select(r => r.HeartRate));
        Assert.Null(rows[1].Cuff);
        Assert.Equal(new CuffReading(118, 76, null, At(660).ToUnixTimeSeconds()), rows[2].Cuff);
    }

    [Fact]
    public async Task A_patient_who_came_back_is_there_after_a_restart_under_the_same_id()
    {
        var before = Start();
        var patient = await AdmittedAndDischargedAsync(before);
        await before.MessageAsync(Monitor, "321");
        var back = (await before.Router.ReadmitAsync("321", patient.PatientId, 7, CancellationToken.None)).Patient!;

        var after = await RestartAsync(before);

        Assert.Equal(back, after.OnlyPatient);
        Assert.Equal(patient.PatientId, after.Bindings.Get(Monitor)!.PatientId);

        Assert.Equal(patient.PatientId, await after.ValuesAsync(Monitor, Values(91)));
        after.Tick(900);
        Assert.Equal(new int?[] { 80, 91 }, after.Rows(patient.PatientId).Select(r => r.HeartRate));
    }

    [Fact]
    public async Task Nobody_unknown_is_admitted_again_and_nobody_who_is_admitted_now()
    {
        var server = Start();
        await server.MessageAsync(Monitor, "321");
        var admitted = (await server.AdmitAsync("321", bed: 4)).Patient!;
        await server.MessageAsync(OtherMonitor, "654");

        // An id nobody was admitted under: mistyped, or of another hospital's server.
        var unknown = await server.Router.ReadmitAsync("654", Guid.NewGuid().ToString(), 7, CancellationToken.None);
        Assert.Equal(AdmitOutcomeKind.EarlierPatientNotFound, unknown.Kind);

        // One person lies in one bed: the answer says which they are to be discharged from.
        var elsewhere = await server.Router.ReadmitAsync("654", admitted.PatientId, 7, CancellationToken.None);
        Assert.Equal(AdmitOutcomeKind.EarlierPatientAdmitted, elsewhere.Kind);
        Assert.Equal(4, elsewhere.Patient!.BedNumber);

        // Either way the other bed stays as it was, and so does the patient.
        Assert.Equal(new[] { admitted.PatientId, "654" }.OrderBy(id => id), server.Patients.GetAll().Select(p => p.PatientId).OrderBy(id => id));
        Assert.Null(server.Patients.Get("654")!.AdmittedAt);
        Assert.Equal("654", server.Bindings.Get(OtherMonitor)!.PatientId);
        Assert.Equal(admitted.PatientId, server.Bindings.Get(Monitor)!.PatientId);
        var recorded = Assert.Single(server.Store!.GetActive());
        Assert.Equal(Monitor, recorded.MonitorIp);
        Assert.Single(server.Store.GetAdmissions(new[] { recorded.Num })[recorded.Num]);
    }

    [Fact]
    public async Task Coming_back_needs_a_bed_that_is_not_admitted_a_monitor_and_a_database_like_any_admission()
    {
        var server = Start();
        var patient = await AdmittedAndDischargedAsync(server);
        await server.MessageAsync(Monitor, "321");
        var other = (await server.AdmitAsync("321", drAlexId: "DA-1002")).Patient!;

        Task<AdmitOutcome> BackOn(string entry) => server.Router.ReadmitAsync(entry, patient.PatientId, 7, CancellationToken.None);

        Assert.Equal(AdmitOutcomeKind.PatientNotFound, (await BackOn("NOBODY")).Kind);
        Assert.Equal(AdmitOutcomeKind.AlreadyAdmitted, (await BackOn(other.PatientId)).Kind);   // the bed is somebody else's

        await server.Pipeline.UpsertPatientAsync(new PatientInfoDto("WAITING", "Doe", 9, null), CancellationToken.None);
        Assert.Equal(AdmitOutcomeKind.NoMonitor, (await BackOn("WAITING")).Kind);

        // A database that cannot be written: nobody is admitted, and the bed is as it was.
        await server.MessageAsync(OtherMonitor, "654");
        server.Store!.FailAdmit = true;
        Assert.Equal(AdmitOutcomeKind.HistoryUnavailable, (await BackOn("654")).Kind);
        Assert.Null(server.Patients.Get("654")!.AdmittedAt);
        Assert.Equal("654", server.Bindings.Get(OtherMonitor)!.PatientId);

        Assert.NotNull(server.Store.GetPatient(patient.PatientId)!.DischargedAt);
        Assert.Equal(other.PatientId, Assert.Single(server.Store.GetActive()).Id);
    }

    // ------------------------------------------------------------------ edit

    [Fact]
    public async Task An_edit_keeps_the_admission_and_is_there_after_a_restart()
    {
        var server = Start();
        await server.MessageAsync(Monitor, "321");
        var patient = (await server.AdmitAsync("321")).Patient!;

        var outcome = await server.Router.EditAsync(
            patient.PatientId, new PatientEntry("DA-2002", "Іваненко І. І.", 6, "Ped", null), CancellationToken.None);

        Assert.Equal(EditOutcomeKind.Updated, outcome.Kind);
        var edited = server.OnlyPatient;
        Assert.Equal(outcome.Patient, edited);
        Assert.Equal(patient.PatientId, edited.PatientId);
        Assert.Equal(patient.AdmittedAt, edited.AdmittedAt);
        Assert.Equal("DA-2002", edited.DrAlexId);
        Assert.Equal(6, edited.BedNumber);
        Assert.Null(edited.Diagnosis);

        var next = await RestartAsync(server);
        Assert.Equal(edited, next.OnlyPatient);
    }

    [Fact]
    public async Task An_edit_that_cannot_be_written_changes_nothing()
    {
        var server = Start();
        await server.MessageAsync(Monitor, "321");
        var patient = (await server.AdmitAsync("321")).Patient!;
        server.Store!.FailUpdate = true;

        var outcome = await server.Router.EditAsync(
            patient.PatientId, new PatientEntry("DA-2002", "Other", 6, "Ped", null), CancellationToken.None);

        Assert.Equal(EditOutcomeKind.HistoryUnavailable, outcome.Kind);
        Assert.Equal(patient, server.OnlyPatient);
    }

    [Fact]
    public async Task Editing_somebody_who_is_not_admitted_does_not_admit_them()
    {
        var server = Start();
        await server.MessageAsync(Monitor, "321");

        var outcome = await server.Router.EditAsync(
            "321", new PatientEntry("DA-2002", "Doe Jane", 6, "Adu", "MI"), CancellationToken.None);

        Assert.Equal(EditOutcomeKind.Updated, outcome.Kind);
        var edited = server.OnlyPatient;
        Assert.Equal("Doe Jane", edited.PatientName);
        Assert.Null(edited.DrAlexId);
        Assert.Null(edited.AdmittedAt);
        Assert.Empty(server.Store!.GetActive());

        Assert.Equal(
            EditOutcomeKind.PatientNotFound,
            (await server.Router.EditAsync("NOBODY", new PatientEntry(null, "X", 1, "Adu", null), CancellationToken.None)).Kind);
    }

    // ------------------------------------------------------------------ restart

    [Fact]
    public async Task A_restart_brings_admitted_patients_back_on_their_monitors_and_the_recording_goes_on()
    {
        var before = Start();
        await before.MessageAsync(Monitor, "321");
        var patient = (await before.AdmitAsync("321")).Patient!;
        await before.ValuesAsync(Monitor, Values(80));
        before.Tick(1);

        var after = await RestartAsync(before);

        // Back before any monitor has spoken, exactly as entered.
        Assert.Equal(patient, after.OnlyPatient);
        Assert.Equal(patient.PatientId, after.Bindings.Get(Monitor)!.PatientId);

        // The monitor's first messages find them there: no entry for somebody unknown, no notice.
        Assert.Equal(patient.PatientId, await after.MessageAsync(Monitor, "321"));
        Assert.Equal(patient.PatientId, await after.ValuesAsync(Monitor, Values(81)));
        after.Tick(60);

        Assert.Equal(patient, after.OnlyPatient);
        Assert.Empty(after.Alerts.GetActiveAlerts());
        Assert.Equal(new int?[] { 80, 81 }, after.Rows(patient.PatientId).Select(r => r.HeartRate));
    }

    [Fact]
    public async Task A_discharged_patient_does_not_come_back_after_a_restart()
    {
        var before = Start();
        await before.MessageAsync(Monitor, "321");
        var patient = (await before.AdmitAsync("321")).Patient!;
        await before.Router.DischargeAsync(patient.PatientId, CancellationToken.None);

        var after = await RestartAsync(before);

        Assert.Empty(after.Patients.GetAll());
        Assert.Null(after.Bindings.Get(Monitor));
    }

    [Fact]
    public async Task A_restored_patient_is_flagged_only_once_the_monitor_has_stayed_silent()
    {
        var before = Start();
        await before.MessageAsync(Monitor, "321");
        var patient = (await before.AdmitAsync("321")).Patient!;

        var after = await RestartAsync(before);
        var restoredAt = DateTimeOffset.UtcNow;

        // Not on the first look after the restart: the monitor has had no time to speak.
        await after.Router.SweepSilentAsync(restoredAt.AddSeconds(5), TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.Empty(after.Alerts.GetActiveAlerts());

        await after.Router.SweepSilentAsync(restoredAt.AddSeconds(40), TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.Equal(
            AlertDto.BuildMonitorSilentId(patient.PatientId),
            Assert.Single(after.Alerts.GetActiveAlerts()).AlertId);
    }

    [Fact]
    public async Task A_monitor_that_spoke_before_the_patients_were_put_back_loses_its_stand_in()
    {
        var before = Start();
        await before.MessageAsync(Monitor, "321");
        var patient = (await before.AdmitAsync("321")).Patient!;
        before.History.Dispose();

        var after = Start();
        await after.MessageAsync(Monitor, "321");            // heard first
        await after.Router.RestoreAsync(CancellationToken.None);

        Assert.Equal(patient, after.OnlyPatient);
        Assert.Equal(patient.PatientId, await after.MessageAsync(Monitor, "321"));
    }

    // ------------------------------------------------------------------ the roster file, transfers

    [Fact]
    public async Task An_admitted_patient_is_not_kept_in_the_roster_file()
    {
        var server = Start(new JsonFilePatientRecordStore(RosterFile));
        await server.MessageAsync(Monitor, "321");
        Assert.NotNull(new JsonFilePatientRecordStore(RosterFile).Get("321"));

        var patient = (await server.AdmitAsync("321")).Patient!;

        // The history database is where they are kept; two records of one patient would disagree.
        var file = new JsonFilePatientRecordStore(RosterFile);
        Assert.Null(file.Get(patient.PatientId));
        Assert.Null(file.Get("321"));
    }

    [Fact]
    public async Task An_admitted_patient_moved_to_another_monitor_is_put_back_there_after_a_restart()
    {
        var before = Start();
        await before.MessageAsync(Monitor, "321");
        await before.MessageAsync(OtherMonitor, string.Empty);
        await before.ValuesAsync(OtherMonitor, Values(60, systolic: 160, diastolic: 100, cuffTakenAt: -60));
        var patient = (await before.AdmitAsync("321")).Patient!;

        var outcome = await before.Router.TransferAsync(patient.PatientId, OtherMonitor, CancellationToken.None);

        Assert.Equal(TransferOutcomeKind.Moved, outcome.Kind);
        Assert.Equal(patient, before.OnlyPatient);            // the anonymous stand-in on that monitor is gone
        Assert.Equal(OtherMonitor, before.Store!.GetPatient(patient.PatientId)!.MonitorIp);

        // Held there as well, and the reading that monitor already showed is not theirs.
        Assert.Equal(patient.PatientId, await before.MessageAsync(OtherMonitor, "777"));
        await before.ValuesAsync(OtherMonitor, Values(61, systolic: 160, diastolic: 100, cuffTakenAt: -60));
        before.Tick(1);
        Assert.Null(Assert.Single(before.Rows(patient.PatientId)).Cuff);

        var after = await RestartAsync(before);
        Assert.Equal(patient.PatientId, after.Bindings.Get(OtherMonitor)!.PatientId);
        Assert.Null(after.Bindings.Get(Monitor));
    }
}
