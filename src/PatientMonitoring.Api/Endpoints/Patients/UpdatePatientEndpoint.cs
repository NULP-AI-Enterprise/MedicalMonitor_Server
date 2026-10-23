using FastEndpoints;
using PatientMonitoring.Api.Contracts;
using PatientMonitoring.Api.Services;

namespace PatientMonitoring.Api.Endpoints.Patients;

public sealed class UpdatePatientEndpoint(IPatientService patients)
    : Endpoint<UpdatePatientRequest, PatientResponse>
{
    public override void Configure()
    {
        Put("/api/patients/{id:guid}");
        AllowAnonymous();
    }

    public override async Task HandleAsync(UpdatePatientRequest req, CancellationToken ct)
    {
        var id = Route<Guid>("id");
        var updated = await patients.UpdateAsync(id, req, ct);

        if (updated is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(updated, ct);
    }
}
