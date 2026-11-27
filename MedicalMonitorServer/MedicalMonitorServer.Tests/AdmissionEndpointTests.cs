using FastEndpoints;
using MedicalMonitorServer.Contracts.Api;
using MedicalMonitorServer.Contracts.Models;
using MedicalMonitorServer.Core.Monitoring;
using MedicalMonitorServer.Dal.History;
using MedicalMonitorServer.Endpoints;
using MedicalMonitorServer.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace MedicalMonitorServer.Tests;

/// <summary>
/// What a station gets back when it admits, edits or discharges a patient, and when it reads
/// the recorded history. The answers matter: a station shows them to the nurse, and "admitted"
/// must never be said for an admission that was not written down.
/// </summary>
public sealed class AdmissionEndpointTests : IDisposable
{
    private const string Monitor = "10.10.46.48";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "mm-admission-" + Guid.NewGuid().ToString("N"));

    private readonly FakeHubContext _hub = new();
    private readonly PatientRegistry _patients = new();
    private readonly MonitorBindings _bindings = new();
    private readonly VitalsRecorder _recorder = new();
    private readonly FlakyHistoryStore _store;
    private readonly PatientHistory _history;
    private readonly MonitorPipeline _pipeline;
    private readonly MonitorRouter _router;

    public AdmissionEndpointTests()
    {
        _store = new FlakyHistoryStore(new SqliteHistoryStore(Path.Combine(_folder, "data", "history.db")));
        _history = new PatientHistory(_store, _recorder, NullLogger<PatientHistory>.Instance);
        _pipeline = new MonitorPipeline(
            _hub, _patients, new AlertStateService(), _bindings, new FeedStatus(), NullLogger<MonitorPipeline>.Instance,
            recorder: _recorder);
        _router = new MonitorRouter(_bindings, _pipeline, _patients, NullLogger<MonitorRouter>.Instance, history: _history);
    }

    public void Dispose()
    {
        _history.Dispose();
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    /// <summary>A monitor reporting patient "321": the bed a nurse is looking at.</summary>
    private Task MonitorReportsAsync(string reportedId = "321") =>
        _router.ResolveAsync(
            Monitor, new PatientInfo { PidPresent = true, PatientId = reportedId }, hasReadings: true, CancellationToken.None);

    /// <summary>A second monitor, with a patient of its own.</summary>
    private Task AnotherMonitorReportsAsync(string reportedId) =>
        _router.ResolveAsync(
            "10.10.46.49", new PatientInfo { PidPresent = true, PatientId = reportedId }, hasReadings: true, CancellationToken.None);

    private async Task<PatientInfoDto> AdmittedAsync()
    {
        await MonitorReportsAsync();
        var outcome = await _router.AdmitAsync("321", new PatientEntry("DA-1001", "Іваненко Іван", 4, "Adu", null), CancellationToken.None);
        return outcome.Patient!;
    }

    private static int Status(BaseEndpoint endpoint) => endpoint.HttpContext.Response.StatusCode;

    // ------------------------------------------------------------------ admit

    private async Task<AdmitPatientEndpoint> AdmitAsync(AdmitPatientRequest request)
    {
        var endpoint = Factory.Create<AdmitPatientEndpoint>(_router);
        await endpoint.HandleAsync(request, default);
        return endpoint;
    }

    [Fact]
    public async Task Admitting_answers_201_with_the_patient_under_their_new_id()
    {
        await MonitorReportsAsync();

        var endpoint = await AdmitAsync(new AdmitPatientRequest
        {
            PatientId = "321", DrAlexId = "  DA-1001 ", PatientName = " Іваненко Іван ", BedNumber = 4, Diagnosis = " ",
        });

        Assert.Equal(StatusCodes.Status201Created, Status(endpoint));
        var patient = Assert.Single(_patients.GetAll());
        Assert.Equal(patient, endpoint.Response);
        Assert.NotEqual("321", patient.PatientId);
        Assert.Equal("DA-1001", patient.DrAlexId);              // as typed, without the blanks around it
        Assert.Equal("Іваненко Іван", patient.PatientName);
        Assert.Null(patient.Diagnosis);                          // a field left empty is no value
        Assert.NotNull(patient.AdmittedAt);
    }

    [Theory]
    [InlineData("DA-1001", null)]
    [InlineData(null, "Іваненко Іван")]
    public async Task One_of_the_DrAlex_id_and_the_name_is_enough(string? drAlexId, string? name)
    {
        await MonitorReportsAsync();

        var endpoint = await AdmitAsync(new AdmitPatientRequest { PatientId = "321", DrAlexId = drAlexId, PatientName = name, BedNumber = 1 });

        Assert.Equal(StatusCodes.Status201Created, Status(endpoint));
        Assert.NotNull(Assert.Single(_patients.GetAll()).AdmittedAt);
    }

    [Theory]
    [InlineData(null, " ", 1, "Adu")]        // nothing was entered about them
    [InlineData("DA-1", "Doe", 0, "Adu")]    // no bed
    [InlineData("DA-1", "Doe", 1, "Old")]    // no such patient type
    public async Task An_admission_with_nothing_or_nonsense_in_it_answers_400(string? drAlexId, string? name, int bed, string type)
    {
        await MonitorReportsAsync();

        var endpoint = await AdmitAsync(new AdmitPatientRequest
        {
            PatientId = "321", DrAlexId = drAlexId, PatientName = name, BedNumber = bed, PatientType = type,
        });

        Assert.Equal(StatusCodes.Status400BadRequest, Status(endpoint));
        Assert.Null(Assert.Single(_patients.GetAll()).AdmittedAt);
        Assert.Empty(_store.GetActive());
    }

    [Fact]
    public async Task Admitting_somebody_who_is_not_on_the_roster_answers_404()
    {
        var endpoint = await AdmitAsync(new AdmitPatientRequest { PatientId = "NOBODY", DrAlexId = "DA-1", BedNumber = 1 });

        Assert.Equal(StatusCodes.Status404NotFound, Status(endpoint));
    }

    [Fact]
    public async Task Admitting_somebody_twice_or_somebody_on_no_monitor_answers_409()
    {
        var patient = await AdmittedAsync();
        await _pipeline.UpsertPatientAsync(new PatientInfoDto("WAITING", "Doe", 9, null), CancellationToken.None);

        var twice = await AdmitAsync(new AdmitPatientRequest { PatientId = patient.PatientId, DrAlexId = "DA-1", BedNumber = 1 });
        var noMonitor = await AdmitAsync(new AdmitPatientRequest { PatientId = "WAITING", DrAlexId = "DA-2", BedNumber = 9 });

        Assert.Equal(StatusCodes.Status409Conflict, Status(twice));
        Assert.Equal(StatusCodes.Status409Conflict, Status(noMonitor));
        Assert.Single(_store.GetActive());
    }

    [Fact]
    public async Task An_admission_that_cannot_be_written_down_answers_503_and_admits_nobody()
    {
        await MonitorReportsAsync();
        _store.FailAdmit = true;

        var endpoint = await AdmitAsync(new AdmitPatientRequest { PatientId = "321", DrAlexId = "DA-1", BedNumber = 1 });

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Status(endpoint));
        Assert.Null(Assert.Single(_patients.GetAll()).AdmittedAt);
    }

    // ------------------------------------------------------------------ admit again

    /// <summary>Somebody admitted and discharged; their monitor now reports the next patient, "654".</summary>
    private async Task<PatientInfoDto> DischargedAsync()
    {
        var patient = await AdmittedAsync();
        await _router.DischargeAsync(patient.PatientId, CancellationToken.None);
        await MonitorReportsAsync("654");
        return patient;
    }

    [Fact]
    public async Task Admitting_an_earlier_patient_again_answers_201_with_them_under_the_id_they_had()
    {
        var earlier = await DischargedAsync();

        // Pasted as it came out of some other program: in capitals, with a blank behind it.
        // Whatever else the form sends is not who they are.
        var endpoint = await AdmitAsync(new AdmitPatientRequest
        {
            PatientId = "654",
            ExistingPatientId = earlier.PatientId.ToUpperInvariant() + " ",
            BedNumber = 7,
            DrAlexId = "DA-OTHER",
            PatientName = "Somebody Else",
            PatientType = "Neo",
        });

        Assert.Equal(StatusCodes.Status201Created, Status(endpoint));
        var patient = Assert.Single(_patients.GetAll());
        Assert.Equal(patient, endpoint.Response);
        Assert.Equal(earlier.PatientId, patient.PatientId);
        Assert.Equal("DA-1001", patient.DrAlexId);
        Assert.Equal("Іваненко Іван", patient.PatientName);
        Assert.Equal("Adu", patient.PatientType);
        Assert.Equal(7, patient.BedNumber);
        Assert.NotNull(patient.AdmittedAt);
        Assert.Equal(earlier.PatientId, Assert.Single(_store.GetPatients(null, 10)).Id);   // nobody new in the history
    }

    [Fact]
    public async Task Admitting_an_earlier_patient_again_needs_only_a_bed()
    {
        var earlier = await DischargedAsync();

        var noBed = await AdmitAsync(new AdmitPatientRequest { PatientId = "654", ExistingPatientId = earlier.PatientId });
        Assert.Equal(StatusCodes.Status400BadRequest, Status(noBed));
        Assert.Empty(_store.GetActive());

        var bedOnly = await AdmitAsync(new AdmitPatientRequest { PatientId = "654", ExistingPatientId = earlier.PatientId, BedNumber = 2 });
        Assert.Equal(StatusCodes.Status201Created, Status(bedOnly));
    }

    [Fact]
    public async Task An_id_nobody_was_admitted_under_answers_400_and_admits_nobody()
    {
        await DischargedAsync();

        var endpoint = await AdmitAsync(new AdmitPatientRequest
        {
            PatientId = "654", ExistingPatientId = "0b9c5e2a-6d0f-4c56-9f0e-3a2f8f1d7c11", BedNumber = 7, DrAlexId = "DA-1", PatientName = "Doe",
        });

        // Not admitted as somebody new on the strength of the other fields: the station asked for one patient.
        Assert.Equal(StatusCodes.Status400BadRequest, Status(endpoint));
        Assert.Contains(endpoint.ValidationFailures, failure => failure.ErrorMessage.Contains("Nobody was ever admitted"));
        Assert.Null(Assert.Single(_patients.GetAll()).AdmittedAt);
        Assert.Empty(_store.GetActive());
    }

    [Fact]
    public async Task An_earlier_patient_who_is_admitted_now_answers_409_and_says_where()
    {
        var admitted = await AdmittedAsync();                       // on bed 4
        await AnotherMonitorReportsAsync("654");

        var endpoint = await AdmitAsync(new AdmitPatientRequest { PatientId = "654", ExistingPatientId = admitted.PatientId, BedNumber = 7 });

        Assert.Equal(StatusCodes.Status409Conflict, Status(endpoint));
        Assert.Contains(endpoint.ValidationFailures, failure => failure.ErrorMessage.Contains("bed 4"));
        Assert.Equal(admitted.PatientId, Assert.Single(_store.GetActive()).Id);
        Assert.Null(_patients.Get("654")!.AdmittedAt);
    }

    // ------------------------------------------------------------------ create, edit, discharge

    [Fact]
    public async Task A_patient_posted_as_admitted_is_added_as_not_admitted()
    {
        // Nobody is admitted by saying so: that takes a monitor and a row in the history.
        var endpoint = Factory.Create<CreatePatientEndpoint>(_patients, _pipeline);

        await endpoint.HandleAsync(
            new PatientInfoDto("P-1", "Doe Jane", 3, null, "Adu", DrAlexId: "DA-9", AdmittedAt: DateTimeOffset.UtcNow), default);

        Assert.Equal(StatusCodes.Status201Created, Status(endpoint));
        var patient = Assert.Single(_patients.GetAll());
        Assert.Null(patient.AdmittedAt);
        Assert.Null(patient.DrAlexId);
        Assert.Empty(_store.GetActive());
    }

    private async Task<UpdatePatientEndpoint> UpdateAsync(UpdatePatientRequest request)
    {
        var endpoint = Factory.Create<UpdatePatientEndpoint>(_patients, _router);
        await endpoint.HandleAsync(request, default);
        return endpoint;
    }

    [Fact]
    public async Task An_admitted_patient_may_go_by_their_DrAlex_id_alone_and_stays_admitted()
    {
        var patient = await AdmittedAsync();

        var endpoint = await UpdateAsync(new UpdatePatientRequest
        {
            PatientId = patient.PatientId, PatientName = "", DrAlexId = "DA-2002", BedNumber = 6, PatientType = "Ped",
        });

        Assert.Equal(StatusCodes.Status200OK, Status(endpoint));
        var edited = Assert.Single(_patients.GetAll());
        Assert.Equal(edited, endpoint.Response);
        Assert.Equal(patient.AdmittedAt, edited.AdmittedAt);
        Assert.Equal("DA-2002", edited.DrAlexId);
        Assert.Equal(string.Empty, edited.PatientName);
        Assert.Equal("DA-2002", _store.GetPatient(patient.PatientId)!.DrAlexId);
    }

    [Fact]
    public async Task An_admitted_patient_cannot_be_left_with_neither_a_name_nor_a_DrAlex_id()
    {
        var patient = await AdmittedAsync();

        var endpoint = await UpdateAsync(new UpdatePatientRequest { PatientId = patient.PatientId, PatientName = " ", BedNumber = 6 });

        Assert.Equal(StatusCodes.Status400BadRequest, Status(endpoint));
        Assert.Equal(patient, Assert.Single(_patients.GetAll()));
    }

    [Fact]
    public async Task Somebody_who_is_not_admitted_still_needs_a_name_and_gets_no_DrAlex_id()
    {
        await MonitorReportsAsync();

        var nameless = await UpdateAsync(new UpdatePatientRequest { PatientId = "321", PatientName = "", DrAlexId = "DA-1", BedNumber = 2 });
        var named = await UpdateAsync(new UpdatePatientRequest { PatientId = "321", PatientName = "Doe Jane", DrAlexId = "DA-1", BedNumber = 2 });
        var nobody = await UpdateAsync(new UpdatePatientRequest { PatientId = "NOBODY", PatientName = "X", BedNumber = 2 });

        Assert.Equal(StatusCodes.Status400BadRequest, Status(nameless));
        Assert.Equal(StatusCodes.Status200OK, Status(named));
        Assert.Equal(StatusCodes.Status404NotFound, Status(nobody));
        var patient = Assert.Single(_patients.GetAll());
        Assert.Equal("Doe Jane", patient.PatientName);
        Assert.Null(patient.DrAlexId);
        Assert.Null(patient.AdmittedAt);
    }

    private async Task<DeletePatientEndpoint> DischargeAsync(string patientId)
    {
        var endpoint = Factory.Create<DeletePatientEndpoint>(_router);
        await endpoint.HandleAsync(new PatientRouteRequest { PatientId = patientId }, default);
        return endpoint;
    }

    [Fact]
    public async Task Discharging_answers_204_and_ends_the_recording()
    {
        var patient = await AdmittedAsync();

        var endpoint = await DischargeAsync(patient.PatientId);

        Assert.Equal(StatusCodes.Status204NoContent, Status(endpoint));
        Assert.Empty(_patients.GetAll());
        Assert.Empty(_store.GetActive());
        Assert.Equal(StatusCodes.Status404NotFound, Status(await DischargeAsync(patient.PatientId)));
    }

    [Fact]
    public async Task A_discharge_that_cannot_be_written_down_answers_503_and_the_patient_stays()
    {
        var patient = await AdmittedAsync();
        _store.FailDischarge = true;

        var endpoint = await DischargeAsync(patient.PatientId);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Status(endpoint));
        Assert.Equal(patient, Assert.Single(_patients.GetAll()));
    }

    // ------------------------------------------------------------------ reading the history

    [Fact]
    public async Task The_recorded_patients_are_listed_the_last_admitted_first_with_the_discharged_among_them()
    {
        var first = await AdmittedAsync();
        await _router.DischargeAsync(first.PatientId, CancellationToken.None);
        await MonitorReportsAsync();
        var second = (await _router.AdmitAsync("321", new PatientEntry("DA-1002", "", 4, "Adu", null), CancellationToken.None)).Patient!;

        var endpoint = Factory.Create<GetHistoryPatientsEndpoint>(_history);
        await endpoint.HandleAsync(new HistoryPatientsRequest(), default);

        Assert.Equal(StatusCodes.Status200OK, Status(endpoint));
        Assert.Equal(new[] { second.PatientId, first.PatientId }.OrderBy(id => id), endpoint.Response.Select(p => p.PatientId).OrderBy(id => id));
        var discharged = endpoint.Response.Single(p => p.PatientId == first.PatientId);
        Assert.NotNull(discharged.DischargedAt);
        Assert.Null(discharged.MonitorIp);
        Assert.Equal("Іваненко Іван", discharged.PatientName);
        var onMonitor = endpoint.Response.Single(p => p.PatientId == second.PatientId);
        Assert.Null(onMonitor.DischargedAt);
        Assert.Equal(Monitor, onMonitor.MonitorIp);
        Assert.Equal(string.Empty, onMonitor.PatientName);

        var byDrAlexId = Factory.Create<GetHistoryPatientsEndpoint>(_history);
        await byDrAlexId.HandleAsync(new HistoryPatientsRequest { DrAlexId = "DA-1002" }, default);
        Assert.Equal(second.PatientId, Assert.Single(byDrAlexId.Response).PatientId);

        // Each comes with their stays: one apiece here.
        Assert.All(endpoint.Response, p => Assert.Equal(p.AdmittedAt, Assert.Single(p.Admissions).AdmittedAt));
        Assert.Equal(discharged.DischargedAt, discharged.Admissions[0].DischargedAt);
        Assert.Equal(new AdmissionDto(onMonitor.AdmittedAt, null, 4, Monitor), onMonitor.Admissions[0]);
    }

    private async Task<List<RecordedPatientDto>> DischargedListAsync(HistoryPatientsRequest? request = null)
    {
        var endpoint = Factory.Create<GetHistoryPatientsEndpoint>(_history);
        await endpoint.HandleAsync(request ?? new HistoryPatientsRequest { Discharged = true }, default);
        Assert.Equal(StatusCodes.Status200OK, Status(endpoint));
        return endpoint.Response;
    }

    [Fact]
    public async Task The_discharged_are_listed_the_last_discharged_first_for_a_station_to_admit_again()
    {
        // Straight into the database, where the time of a discharge can be chosen: years ago,
        // so that whoever a station discharges in this test left after them.
        var start = new DateTimeOffset(2020, 1, 1, 10, 0, 0, TimeSpan.Zero);
        foreach (var (id, drAlexId, leftAfterHours) in new[] { ("uuid-1", "DA-7", 2), ("uuid-2", "DA-8", 1), ("uuid-3", "DA-7", 3) })
        {
            _store.Admit(new NewRecordedPatient(id, drAlexId, "Doe " + id, 4, "Adu", null, Monitor, start));
            _store.Discharge(id, start.AddHours(leftAfterHours));
        }

        var onMonitor = await AdmittedAsync();

        var listed = await DischargedListAsync();
        Assert.Equal(new[] { "uuid-3", "uuid-1", "uuid-2" }, listed.Select(p => p.PatientId));
        Assert.DoesNotContain(listed, p => p.PatientId == onMonitor.PatientId);
        Assert.Equal("Doe uuid-3", listed[0].PatientName);
        Assert.Equal(start.AddHours(3), listed[0].DischargedAt);
        Assert.Equal(new AdmissionDto(start, start.AddHours(3), 4, Monitor), Assert.Single(listed[0].Admissions));

        Assert.Equal(
            new[] { "uuid-3", "uuid-1" },
            (await DischargedListAsync(new HistoryPatientsRequest { Discharged = true, Limit = 2 })).Select(p => p.PatientId));
        Assert.Equal(
            new[] { "uuid-3", "uuid-1" },
            (await DischargedListAsync(new HistoryPatientsRequest { Discharged = true, DrAlexId = " DA-7 " })).Select(p => p.PatientId));

        // Admitted again, a patient is no longer on offer; discharged again, they are the first.
        await AnotherMonitorReportsAsync("654");
        await AdmitAsync(new AdmitPatientRequest { PatientId = "654", ExistingPatientId = "uuid-2", BedNumber = 5 });
        Assert.DoesNotContain(await DischargedListAsync(), p => p.PatientId == "uuid-2");

        await _router.DischargeAsync("uuid-2", CancellationToken.None);
        var again = (await DischargedListAsync())[0];
        Assert.Equal("uuid-2", again.PatientId);
        Assert.Equal(2, again.Admissions.Count);
        Assert.Equal(5, again.Admissions[1].BedNumber);
    }

    private async Task<GetHistoryPatientEndpoint> RecordedPatientAsync(string patientId)
    {
        var endpoint = Factory.Create<GetHistoryPatientEndpoint>(_history);
        await endpoint.HandleAsync(new PatientRouteRequest { PatientId = patientId }, default);
        return endpoint;
    }

    [Fact]
    public async Task A_recorded_patient_is_read_by_their_id_with_every_stay_of_theirs()
    {
        var earlier = await DischargedAsync();
        await AdmitAsync(new AdmitPatientRequest { PatientId = "654", ExistingPatientId = earlier.PatientId, BedNumber = 7 });

        // As pasted: the server knows the id in any of the ways it is written.
        var endpoint = await RecordedPatientAsync($"  {{{earlier.PatientId.ToUpperInvariant()}}} ");

        Assert.Equal(StatusCodes.Status200OK, Status(endpoint));
        var patient = endpoint.Response;
        Assert.Equal(earlier.PatientId, patient.PatientId);
        Assert.Equal("DA-1001", patient.DrAlexId);
        Assert.Equal("Іваненко Іван", patient.PatientName);
        Assert.Equal(7, patient.BedNumber);
        Assert.Equal(Monitor, patient.MonitorIp);
        Assert.Null(patient.DischargedAt);

        Assert.Equal(2, patient.Admissions.Count);
        Assert.Equal(earlier.AdmittedAt, patient.Admissions[0].AdmittedAt);
        Assert.NotNull(patient.Admissions[0].DischargedAt);
        Assert.Equal(4, patient.Admissions[0].BedNumber);
        Assert.Equal(new AdmissionDto(patient.AdmittedAt, null, 7, Monitor), patient.Admissions[1]);
    }

    [Theory]
    [InlineData("0b9c5e2a-6d0f-4c56-9f0e-3a2f8f1d7c11")]   // nobody was admitted under it
    [InlineData("321")]                                     // what a monitor calls its patient is not an id of the history
    public async Task An_id_the_history_does_not_have_answers_404(string patientId)
    {
        await MonitorReportsAsync();

        Assert.Equal(StatusCodes.Status404NotFound, Status(await RecordedPatientAsync(patientId)));
    }

    [Fact]
    public async Task Without_a_start_the_values_of_every_stay_come_back()
    {
        // The seconds are of the test's choosing: one long before the patient comes back - which
        // the clock on the wall decides - and one long after.
        var firstStay = new DateTimeOffset(2020, 1, 1, 10, 0, 0, TimeSpan.Zero);
        var secondStay = new DateTimeOffset(2040, 1, 1, 10, 0, 0, TimeSpan.Zero);

        var earlier = await AdmittedAsync();
        await _pipeline.IngestReadingsAsync(
            earlier.PatientId, new VitalReadings(HeartRate: 80), null, DateTimeOffset.UtcNow, CancellationToken.None, complete: true);
        _history.Flush(firstStay);
        await _router.DischargeAsync(earlier.PatientId, CancellationToken.None);

        await MonitorReportsAsync("654");
        await AdmitAsync(new AdmitPatientRequest { PatientId = "654", ExistingPatientId = earlier.PatientId, BedNumber = 7 });
        await _pipeline.IngestReadingsAsync(
            earlier.PatientId, new VitalReadings(HeartRate: 90), null, DateTimeOffset.UtcNow, CancellationToken.None, complete: true);
        _history.Flush(secondStay);

        // Asked without a start: from their first admission, not from the stay they are in.
        var endpoint = Factory.Create<GetHistoryVitalsEndpoint>(_history);
        await endpoint.HandleAsync(new HistoryVitalsRequest { PatientId = earlier.PatientId, To = secondStay }, default);

        Assert.Equal(StatusCodes.Status200OK, Status(endpoint));
        Assert.Equal(new int?[] { 80, 90 }, endpoint.Response.Select(r => r.HeartRate));
        Assert.Equal(new[] { firstStay, secondStay }, endpoint.Response.Select(r => r.Time));
    }

    [Fact]
    public async Task The_recorded_values_come_back_a_record_per_second_oldest_first()
    {
        var patient = await AdmittedAsync();
        var start = new DateTimeOffset(2026, 11, 27, 10, 0, 0, TimeSpan.Zero);

        await _pipeline.IngestReadingsAsync(
            patient.PatientId,
            new VitalReadings(
                HeartRate: 80, SpO2: 98, RespiratoryRate: 12, Temperature: 36.5, PulseRate: 79,
                Systolic: 120, Diastolic: 80, MeanArterialPressure: 93, NibpMeasuredAt: start,
                Parameters: new Dictionary<string, ParameterDto> { ["art.sys"] = new("art.sys", "ART Sys", 118, "mmHg", "art") }),
            null, DateTimeOffset.UtcNow, CancellationToken.None, complete: true);
        _history.Flush(start.AddSeconds(1));
        await _pipeline.IngestReadingsAsync(
            patient.PatientId, new VitalReadings(HeartRate: 81, Systolic: 120, Diastolic: 80, MeanArterialPressure: 93, NibpMeasuredAt: start),
            null, DateTimeOffset.UtcNow, CancellationToken.None, complete: true);
        _history.Flush(start.AddSeconds(2));

        var endpoint = Factory.Create<GetHistoryVitalsEndpoint>(_history);
        await endpoint.HandleAsync(
            new HistoryVitalsRequest { PatientId = patient.PatientId, From = start, To = start.AddMinutes(1) }, default);

        Assert.Equal(StatusCodes.Status200OK, Status(endpoint));
        Assert.Equal(2, endpoint.Response.Count);

        var first = endpoint.Response[0];
        Assert.Equal(start.AddSeconds(1), first.Time);
        Assert.Equal(80, first.HeartRate);
        Assert.Equal(98, first.SpO2);
        Assert.Equal(79, first.PulseRate);
        Assert.Equal(12, first.RespiratoryRate);
        Assert.Equal(36.5, first.Temperature);
        Assert.Equal(new BloodPressureDto(120, 80, 93, start), first.Nibp);
        Assert.Equal(118, first.Parameters!["art.sys"]);

        // The reading still stands on the monitor; in the history it is there once.
        var second = endpoint.Response[1];
        Assert.Equal(81, second.HeartRate);
        Assert.Null(second.SpO2);
        Assert.Null(second.Nibp);
        Assert.Null(second.Parameters);

        var newest = Factory.Create<GetHistoryVitalsEndpoint>(_history);
        await newest.HandleAsync(
            new HistoryVitalsRequest { PatientId = patient.PatientId, From = start, To = start.AddMinutes(1), Limit = 1 }, default);
        Assert.Equal(81, Assert.Single(newest.Response).HeartRate);
    }

    [Fact]
    public async Task The_history_of_somebody_who_was_never_admitted_answers_404()
    {
        await MonitorReportsAsync();

        var endpoint = Factory.Create<GetHistoryVitalsEndpoint>(_history);
        await endpoint.HandleAsync(new HistoryVitalsRequest { PatientId = "321" }, default);

        Assert.Equal(StatusCodes.Status404NotFound, Status(endpoint));
    }

    [Fact]
    public async Task A_server_without_a_history_database_answers_503_to_those_who_ask_for_it()
    {
        using var off = new PatientHistory(null, _recorder, NullLogger<PatientHistory>.Instance, configured: false);

        var patients = Factory.Create<GetHistoryPatientsEndpoint>(off);
        await patients.HandleAsync(new HistoryPatientsRequest(), default);
        var vitals = Factory.Create<GetHistoryVitalsEndpoint>(off);
        await vitals.HandleAsync(new HistoryVitalsRequest { PatientId = "321" }, default);
        var patient = Factory.Create<GetHistoryPatientEndpoint>(off);
        await patient.HandleAsync(new PatientRouteRequest { PatientId = "321" }, default);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Status(patients));
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Status(vitals));
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Status(patient));
    }
}
