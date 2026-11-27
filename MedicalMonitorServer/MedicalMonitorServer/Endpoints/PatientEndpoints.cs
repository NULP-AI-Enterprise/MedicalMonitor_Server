using FastEndpoints;
using MedicalMonitorServer.Contracts.Api;
using MedicalMonitorServer.Core.Monitoring;
using MedicalMonitorServer.Services;

namespace MedicalMonitorServer.Endpoints;

public sealed class PatientRouteRequest
{
    public string PatientId { get; set; } = string.Empty;
}

/// <summary>PUT body plus the patient id from the route.</summary>
public sealed class UpdatePatientRequest
{
    public string PatientId { get; set; } = string.Empty;

    public string PatientName { get; set; } = string.Empty;

    public int BedNumber { get; set; }

    public string? Diagnosis { get; set; }

    public string PatientType { get; set; } = "Adu";

    /// <summary>For a patient a station admitted; ignored for anybody else.</summary>
    public string? DrAlexId { get; set; }
}

/// <summary>POST body of an admission, plus the id of the roster entry being admitted from the route.</summary>
public sealed class AdmitPatientRequest
{
    public string PatientId { get; set; } = string.Empty;

    public string? DrAlexId { get; set; }

    public string? PatientName { get; set; }

    public int BedNumber { get; set; }

    public string? Diagnosis { get; set; }

    public string PatientType { get; set; } = "Adu";

    /// <summary>
    /// To admit somebody who was here before into the history they have: the id they were
    /// given then (<see cref="RecordedPatientDto.PatientId"/>). Who they are is taken from the
    /// history; of the rest of the body only <see cref="BedNumber"/> counts.
    /// </summary>
    public string? ExistingPatientId { get; set; }
}

internal static class PatientValidation
{
    public static readonly string[] PatientTypes = { "Adu", "Ped", "Neo" };

    public static string? Validate(string patientId, string patientName, int bedNumber, string patientType)
    {
        if (string.IsNullOrWhiteSpace(patientId)) return "patientId is required.";
        if (string.IsNullOrWhiteSpace(patientName)) return "patientName is required.";
        if (bedNumber < 1) return "bedNumber must be 1 or greater.";
        if (!PatientTypes.Contains(patientType)) return "patientType must be one of: Adu, Ped, Neo.";
        return null;
    }

    /// <summary>
    /// For a patient a station admits or has admitted. They are known by what was typed in
    /// about them, so something must have been: a name, a DrAlex id, or both.
    /// </summary>
    public static string? ValidateAdmitted(PatientEntry entry)
    {
        if (entry.DrAlexId is null && entry.PatientName.Length == 0) return "drAlexId or patientName is required.";
        if (entry.BedNumber < 1) return "bedNumber must be 1 or greater.";
        if (!PatientTypes.Contains(entry.PatientType)) return "patientType must be one of: Adu, Ped, Neo.";
        return null;
    }

    /// <summary>What was typed, without the blanks around it; a field left empty is no value.</summary>
    public static PatientEntry Entry(string? drAlexId, string? patientName, int bedNumber, string? patientType, string? diagnosis) => new(
        NullIfBlank(drAlexId),
        patientName?.Trim() ?? string.Empty,
        bedNumber,
        patientType ?? string.Empty,
        NullIfBlank(diagnosis));

    /// <summary>
    /// The id of a recorded patient as a station sends it - pasted with blanks around it, in
    /// capitals, in braces - in the form the history keeps ids in. Null when nothing was sent.
    /// </summary>
    public static string? RecordedPatientId(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var trimmed = text.Trim();
        return Guid.TryParse(trimmed, out var id) ? id.ToString() : trimmed;
    }

    private static string? NullIfBlank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}

public sealed class GetPatientsEndpoint : EndpointWithoutRequest<List<PatientInfoDto>>
{
    private readonly IPatientRegistry _patients;

    public GetPatientsEndpoint(IPatientRegistry patients) => _patients = patients;

    public override void Configure()
    {
        Get("/api/v1/patients");
        AllowAnonymous();
        Summary(s => s.Summary = "All patients, ordered by bed number");
    }

    public override Task HandleAsync(CancellationToken ct) =>
        Send.OkAsync(_patients.GetAll().ToList(), ct);
}

public sealed class GetPatientEndpoint : Endpoint<PatientRouteRequest, PatientInfoDto>
{
    private readonly IPatientRegistry _patients;

    public GetPatientEndpoint(IPatientRegistry patients) => _patients = patients;

    public override void Configure()
    {
        Get("/api/v1/patients/{patientId}");
        AllowAnonymous();
        Summary(s => s.Summary = "Details of one patient");
    }

    public override async Task HandleAsync(PatientRouteRequest req, CancellationToken ct)
    {
        var patient = _patients.Get(req.PatientId);
        if (patient is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(patient, ct);
    }
}

public sealed class CreatePatientEndpoint : Endpoint<PatientInfoDto, PatientInfoDto>
{
    private readonly IPatientRegistry _patients;
    private readonly IMonitorPipeline _pipeline;

    public CreatePatientEndpoint(IPatientRegistry patients, IMonitorPipeline pipeline)
    {
        _patients = patients;
        _pipeline = pipeline;
    }

    public override void Configure()
    {
        Post("/api/v1/patients");
        AllowAnonymous();
        Summary(s => s.Summary = "Add a patient");
    }

    public override async Task HandleAsync(PatientInfoDto req, CancellationToken ct)
    {
        var error = PatientValidation.Validate(req.PatientId, req.PatientName, req.BedNumber, req.PatientType);
        if (error is not null)
        {
            AddError(error);
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        if (_patients.Get(req.PatientId) is not null)
        {
            AddError(r => r.PatientId, "A patient with this id already exists.");
            await Send.ErrorsAsync(StatusCodes.Status409Conflict, ct);
            return;
        }

        // Nobody becomes an admitted patient by saying so in the body: that takes a monitor and
        // a row in the history (POST .../admit).
        var patient = req with { DrAlexId = null, AdmittedAt = null };
        await _pipeline.UpsertPatientAsync(patient, ct);
        await Send.ResponseAsync(patient, StatusCodes.Status201Created, ct);
    }
}

public sealed class AdmitPatientEndpoint : Endpoint<AdmitPatientRequest, PatientInfoDto>
{
    private readonly MonitorRouter _router;

    public AdmitPatientEndpoint(MonitorRouter router) => _router = router;

    public override void Configure()
    {
        Post("/api/v1/patients/{patientId}/admit");
        AllowAnonymous();
        Summary(s =>
        {
            s.Summary = "Admit the patient on a monitor and start recording their values";
            s.Description =
                "patientId is the roster entry that stands for whoever is on the monitor now (the id the monitor " +
                "reports, or an anonymous one). It is replaced by a patient under an id the server makes up " +
                "(PatientRemoved for the old entry, PatientAdded for the new one), and from then on the patient's " +
                "values are recorded once a second until DELETE /api/v1/patients/{id}. drAlexId or patientName is " +
                "required. With existingPatientId - the id of a patient who was admitted here before and has been " +
                "discharged (GET /api/v1/history/patients?discharged=true lists the last ones) - they are admitted " +
                "again under that id and recorded into the history they have: who they are is taken from it, and " +
                "of the body only bedNumber counts. 201 with the admitted patient; 400 when nobody was ever " +
                "admitted under existingPatientId; 409 when the entry is admitted already or is on no monitor, or " +
                "the earlier patient is admitted now; 503 when the history database is off or cannot be written, " +
                "and then nobody is admitted.";
        });
    }

    public override async Task HandleAsync(AdmitPatientRequest req, CancellationToken ct)
    {
        AdmitOutcome outcome;
        if (PatientValidation.RecordedPatientId(req.ExistingPatientId) is { } earlierPatientId)
        {
            // Who they are was entered when they first came. Where they lie now is new.
            if (req.BedNumber < 1)
            {
                AddError("bedNumber must be 1 or greater.");
                await Send.ErrorsAsync(cancellation: ct);
                return;
            }

            outcome = await _router.ReadmitAsync(req.PatientId, earlierPatientId, req.BedNumber, ct);
        }
        else
        {
            var entry = PatientValidation.Entry(req.DrAlexId, req.PatientName, req.BedNumber, req.PatientType, req.Diagnosis);
            var error = PatientValidation.ValidateAdmitted(entry);
            if (error is not null)
            {
                AddError(error);
                await Send.ErrorsAsync(cancellation: ct);
                return;
            }

            outcome = await _router.AdmitAsync(req.PatientId, entry, ct);
        }

        switch (outcome.Kind)
        {
            case AdmitOutcomeKind.Admitted:
                await Send.ResponseAsync(outcome.Patient!, StatusCodes.Status201Created, ct);
                return;

            case AdmitOutcomeKind.PatientNotFound:
                await Send.NotFoundAsync(ct);
                return;

            case AdmitOutcomeKind.AlreadyAdmitted:
                AddError("This patient is admitted already.");
                await Send.ErrorsAsync(StatusCodes.Status409Conflict, ct);
                return;

            case AdmitOutcomeKind.NoMonitor:
                AddError("This patient is on no monitor; only somebody a monitor is measuring can be admitted.");
                await Send.ErrorsAsync(StatusCodes.Status409Conflict, ct);
                return;

            case AdmitOutcomeKind.EarlierPatientNotFound:
                AddError(r => r.ExistingPatientId, "Nobody was ever admitted here under this patient ID.");
                await Send.ErrorsAsync(cancellation: ct);
                return;

            case AdmitOutcomeKind.EarlierPatientAdmitted:
                AddError(
                    r => r.ExistingPatientId,
                    outcome.Patient is { BedNumber: > 0 } elsewhere
                        ? $"This patient is admitted now, on bed {elsewhere.BedNumber}. Discharge them there first."
                        : "This patient is admitted now. Discharge them first.");
                await Send.ErrorsAsync(StatusCodes.Status409Conflict, ct);
                return;

            default:
                AddError(PatientHistoryErrors.Unavailable + " Nobody was admitted.");
                await Send.ErrorsAsync(StatusCodes.Status503ServiceUnavailable, ct);
                return;
        }
    }
}

public sealed class UpdatePatientEndpoint : Endpoint<UpdatePatientRequest, PatientInfoDto>
{
    private readonly IPatientRegistry _patients;
    private readonly MonitorRouter _router;

    public UpdatePatientEndpoint(IPatientRegistry patients, MonitorRouter router)
    {
        _patients = patients;
        _router = router;
    }

    public override void Configure()
    {
        Put("/api/v1/patients/{patientId}");
        AllowAnonymous();
        Summary(s => s.Summary = "Update a patient");
    }

    public override async Task HandleAsync(UpdatePatientRequest req, CancellationToken ct)
    {
        var existing = _patients.Get(req.PatientId);
        if (existing is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // An admitted patient may go by their DrAlex id alone; anybody else needs a name.
        var entry = PatientValidation.Entry(req.DrAlexId, req.PatientName, req.BedNumber, req.PatientType, req.Diagnosis);
        var error = existing.AdmittedAt is not null
            ? PatientValidation.ValidateAdmitted(entry)
            : PatientValidation.Validate(req.PatientId, req.PatientName, req.BedNumber, req.PatientType);
        if (error is not null)
        {
            AddError(error);
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        var outcome = await _router.EditAsync(req.PatientId, entry, ct);
        switch (outcome.Kind)
        {
            case EditOutcomeKind.Updated:
                await Send.OkAsync(outcome.Patient!, ct);
                return;

            case EditOutcomeKind.PatientNotFound:
                await Send.NotFoundAsync(ct);
                return;

            default:
                AddError(PatientHistoryErrors.Unavailable + " Nothing was changed.");
                await Send.ErrorsAsync(StatusCodes.Status503ServiceUnavailable, ct);
                return;
        }
    }
}

public sealed class DeletePatientEndpoint : Endpoint<PatientRouteRequest>
{
    private readonly MonitorRouter _router;

    public DeletePatientEndpoint(MonitorRouter router) => _router = router;

    public override void Configure()
    {
        Delete("/api/v1/patients/{patientId}");
        AllowAnonymous();
        Summary(s =>
        {
            s.Summary = "Discharge a patient (broadcasts PatientRemoved)";
            s.Description =
                "For a patient a station admitted this ends the recording of their values: their monitor, if " +
                "it keeps sending, shows up as somebody not admitted. They can be admitted again later, into " +
                "the same history (POST .../admit with existingPatientId). 503 when the discharge cannot be " +
                "written to the history database, and the patient then stays admitted. A patient no station " +
                "admitted is only taken off the roster, and is back with their monitor's next report.";
        });
    }

    public override async Task HandleAsync(PatientRouteRequest req, CancellationToken ct)
    {
        switch (await _router.DischargeAsync(req.PatientId, ct))
        {
            case DischargeOutcome.Discharged:
                await Send.NoContentAsync(ct);
                return;

            case DischargeOutcome.PatientNotFound:
                await Send.NotFoundAsync(ct);
                return;

            default:
                AddError(PatientHistoryErrors.Unavailable + " The patient is still admitted.");
                await Send.ErrorsAsync(StatusCodes.Status503ServiceUnavailable, ct);
                return;
        }
    }
}

internal static class PatientHistoryErrors
{
    public const string Unavailable = "The history database is not available on this server.";

    public const string Unreadable = "The history database could not be read.";
}

public sealed class GetLatestVitalsEndpoint : Endpoint<PatientRouteRequest, VitalsDto>
{
    private readonly IPatientRegistry _patients;

    public GetLatestVitalsEndpoint(IPatientRegistry patients) => _patients = patients;

    public override void Configure()
    {
        Get("/api/v1/patients/{patientId}/vitals/latest");
        AllowAnonymous();
        Summary(s => s.Summary = "Latest vitals snapshot of a patient");
    }

    public override async Task HandleAsync(PatientRouteRequest req, CancellationToken ct)
    {
        var readings = _patients.GetReadings(req.PatientId);
        if (readings is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(readings.ToDto(), ct);
    }
}
