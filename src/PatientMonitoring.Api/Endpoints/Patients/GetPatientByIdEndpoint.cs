using FastEndpoints;
using PatientMonitoring.Api.Contracts;
using PatientMonitoring.Api.Services;

namespace PatientMonitoring.Api.Endpoints.Patients;

public sealed class GetPatientByIdEndpoint(IPatientService patients)
    : EndpointWithoutRequest<PatientResponse>
{
    public override void Configure()
    {
        Get("/api/patients/{id:guid}");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var id = Route<Guid>("id");
        var patient = await patients.GetByIdAsync(id, ct);

        if (patient is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(patient, ct);
    }
}
