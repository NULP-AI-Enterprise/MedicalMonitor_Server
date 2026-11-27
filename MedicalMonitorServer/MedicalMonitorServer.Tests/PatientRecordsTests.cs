using MedicalMonitorServer.Contracts.Api;
using MedicalMonitorServer.Contracts.Models;
using MedicalMonitorServer.Core.Monitoring;
using MedicalMonitorServer.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace MedicalMonitorServer.Tests;

/// <summary>
/// What a station entered about a patient must outlive a restart of the server, and a
/// patient no station admitted leaves the roster when - and only when - their monitor
/// discharges them. (A patient a station admitted is another matter: see
/// RecordingLifecycleTests.) A "restart" here is a new registry, bindings, pipeline and
/// router over the same file.
/// </summary>
public sealed class PatientRecordsTests : IDisposable
{
    private const string Monitor = "192.168.2.101";
    private const string OtherMonitor = "192.168.2.102";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "mm-records-" + Guid.NewGuid().ToString("N"));
    private string File => Path.Combine(_folder, "data", "patients.json");

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private sealed class Server
    {
        public PatientRegistry Patients { get; } = new();
        public MonitorBindings Bindings { get; } = new();
        public MonitorPipeline Pipeline { get; }
        public MonitorRouter Router { get; }

        public Server(IPatientRecordStore records)
        {
            Pipeline = new MonitorPipeline(
                new FakeHubContext(), Patients, new AlertStateService(), Bindings, new FeedStatus(),
                NullLogger<MonitorPipeline>.Instance, records);
            Router = new MonitorRouter(
                Bindings, Pipeline, Patients, NullLogger<MonitorRouter>.Instance, records: records);
        }

        public Task<string?> MessageAsync(string monitorIp, string? patientId) =>
            Router.ResolveAsync(
                monitorIp,
                patientId is null ? null : new PatientInfo { PidPresent = true, PatientId = patientId },
                hasReadings: true,
                CancellationToken.None);
    }

    private Server Start() => new(new JsonFilePatientRecordStore(File));

    // ------------------------------------------------------------------ the file

    [Fact]
    public void A_saved_record_is_read_back_by_the_next_store()
    {
        var record = new PatientInfoDto("P-1", "Іваненко І. І.", 4, "Sepsis", "Adu");
        new JsonFilePatientRecordStore(File).Save(record);

        Assert.Equal(record, new JsonFilePatientRecordStore(File).Get("P-1"));
    }

    [Fact]
    public void A_removed_record_is_gone_for_the_next_store()
    {
        var store = new JsonFilePatientRecordStore(File);
        store.Save(new PatientInfoDto("P-1", "A", 1, null, "Adu"));
        store.Save(new PatientInfoDto("P-2", "B", 2, null, "Adu"));

        store.Remove("P-1");

        var next = new JsonFilePatientRecordStore(File);
        Assert.Null(next.Get("P-1"));
        Assert.NotNull(next.Get("P-2"));
    }

    [Fact]
    public void An_anonymous_entry_is_not_kept()
    {
        // Whoever is on that monitor after a restart may be somebody else.
        var id = AnonymousPatient.NewId();
        new JsonFilePatientRecordStore(File).Save(new PatientInfoDto(id, "Петренко", 2, "MI", "Adu"));

        Assert.Null(new JsonFilePatientRecordStore(File).Get(id));
    }

    [Fact]
    public void A_file_that_cannot_be_read_is_put_aside_and_the_store_starts_empty()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(File)!);
        System.IO.File.WriteAllText(File, "{ not json");

        var store = new JsonFilePatientRecordStore(File);

        Assert.Null(store.Get("P-1"));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(File)!, "patients.json.unreadable-*"));

        store.Save(new PatientInfoDto("P-1", "A", 1, null, "Adu"));
        Assert.NotNull(new JsonFilePatientRecordStore(File).Get("P-1"));
    }

    // ------------------------------------------------------------------ restart

    [Fact]
    public async Task What_a_station_entered_is_back_once_the_monitor_reports_the_patient_after_a_restart()
    {
        var before = Start();
        await before.MessageAsync(Monitor, "P-1");
        await before.Pipeline.UpsertPatientAsync(
            new PatientInfoDto("P-1", "Іваненко І. І.", 4, "Sepsis", "Adu"), CancellationToken.None);

        var after = Start();
        Assert.Empty(after.Patients.GetAll());   // nobody until a monitor says so

        await after.MessageAsync(Monitor, "P-1");

        var patient = Assert.Single(after.Patients.GetAll());
        Assert.Equal("Іваненко І. І.", patient.PatientName);
        Assert.Equal(4, patient.BedNumber);
        Assert.Equal("Sepsis", patient.Diagnosis);
    }

    [Fact]
    public async Task Without_a_store_a_restart_starts_from_what_the_monitor_reports()
    {
        var before = new Server(new NoPatientRecordStore());
        await before.MessageAsync(Monitor, "P-1");
        await before.Pipeline.UpsertPatientAsync(
            new PatientInfoDto("P-1", "Іваненко І. І.", 4, "Sepsis", "Adu"), CancellationToken.None);

        var after = new Server(new NoPatientRecordStore());
        await after.MessageAsync(Monitor, "P-1");

        Assert.Null(Assert.Single(after.Patients.GetAll()).Diagnosis);
    }

    // ------------------------------------------------------------------ discharge

    [Fact]
    public async Task A_patient_discharged_on_the_monitor_leaves_the_roster_and_the_records()
    {
        var server = Start();
        await server.MessageAsync(Monitor, "P-1");
        Assert.Single(server.Patients.GetAll());

        // The monitor now says it has no patient id: discharged at the bedside. It still
        // measures, so whoever is attached shows as an unidentified patient.
        await server.MessageAsync(Monitor, string.Empty);

        var left = Assert.Single(server.Patients.GetAll());
        Assert.True(AnonymousPatient.IsAnonymous(left.PatientId));
        Assert.Null(new JsonFilePatientRecordStore(File).Get("P-1"));
    }

    [Fact]
    public async Task The_next_patient_admitted_on_the_monitor_replaces_the_one_before()
    {
        var server = Start();
        await server.MessageAsync(Monitor, "P-1");

        await server.MessageAsync(Monitor, "P-2");

        Assert.Equal("P-2", Assert.Single(server.Patients.GetAll()).PatientId);
        Assert.Null(new JsonFilePatientRecordStore(File).Get("P-1"));
    }

    [Fact]
    public async Task A_patient_displaced_by_an_operators_transfer_stays_on_the_roster()
    {
        var server = Start();
        await server.MessageAsync(Monitor, "P-1");
        await server.MessageAsync(OtherMonitor, "P-2");

        var outcome = await server.Router.TransferAsync("P-2", Monitor, CancellationToken.None);

        Assert.Equal(TransferOutcomeKind.Moved, outcome.Kind);
        Assert.Equal(2, server.Patients.GetAll().Count);
        Assert.NotNull(new JsonFilePatientRecordStore(File).Get("P-1"));
    }
}
