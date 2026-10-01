using FastEndpoints;
using PatientMonitoring.Api.Contracts;
using PatientMonitoring.Api.Services;

namespace PatientMonitoring.Api.Endpoints.Patients;

public sealed class CreatePatientEndpoint(IPatientService patients)
    : Endpoint<CreatePatientRequest, PatientResponse>
{
    public override void Configure()
    {
        Post("/api/patients");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CreatePatientRequest req, CancellationToken ct)
    {
        var created = await patients.CreateAsync(req, ct);
        await Send.CreatedAtAsync<GetPatientByIdEndpoint>(
            routeValues: new { id = created.Id },
            responseBody: created,
            cancellation: ct);
    }
}
