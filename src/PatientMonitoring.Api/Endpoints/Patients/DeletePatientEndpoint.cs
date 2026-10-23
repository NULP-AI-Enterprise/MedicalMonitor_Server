using FastEndpoints;
using PatientMonitoring.Api.Services;

namespace PatientMonitoring.Api.Endpoints.Patients;

public sealed class DeletePatientEndpoint(IPatientService patients)
    : EndpointWithoutRequest
{
    public override void Configure()
    {
        Delete("/api/patients/{id:guid}");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var id = Route<Guid>("id");
        var ok = await patients.DeactivateAsync(id, ct);

        if (!ok)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.NoContentAsync(ct);
    }
}
