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

        await _pipeline.UpsertPatientAsync(req, ct);
        await Send.ResponseAsync(req, StatusCodes.Status201Created, ct);
    }
}

public sealed class UpdatePatientEndpoint : Endpoint<UpdatePatientRequest, PatientInfoDto>
{
    private readonly IPatientRegistry _patients;
    private readonly IMonitorPipeline _pipeline;

    public UpdatePatientEndpoint(IPatientRegistry patients, IMonitorPipeline pipeline)
    {
        _patients = patients;
        _pipeline = pipeline;
    }

    public override void Configure()
    {
        Put("/api/v1/patients/{patientId}");
        AllowAnonymous();
        Summary(s => s.Summary = "Update a patient");
    }

    public override async Task HandleAsync(UpdatePatientRequest req, CancellationToken ct)
    {
        if (_patients.Get(req.PatientId) is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var error = PatientValidation.Validate(req.PatientId, req.PatientName, req.BedNumber, req.PatientType);
        if (error is not null)
        {
            AddError(error);
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        var updated = new PatientInfoDto(req.PatientId, req.PatientName, req.BedNumber, req.Diagnosis, req.PatientType);
        await _pipeline.UpsertPatientAsync(updated, ct);
        await Send.OkAsync(updated, ct);
    }
}

public sealed class DeletePatientEndpoint : Endpoint<PatientRouteRequest>
{
    private readonly IMonitorPipeline _pipeline;

    public DeletePatientEndpoint(IMonitorPipeline pipeline) => _pipeline = pipeline;

    public override void Configure()
    {
        Delete("/api/v1/patients/{patientId}");
        AllowAnonymous();
        Summary(s => s.Summary = "Discharge a patient (broadcasts PatientRemoved)");
    }

    public override async Task HandleAsync(PatientRouteRequest req, CancellationToken ct)
    {
        if (!await _pipeline.RemovePatientAsync(req.PatientId, ct))
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.NoContentAsync(ct);
    }
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
