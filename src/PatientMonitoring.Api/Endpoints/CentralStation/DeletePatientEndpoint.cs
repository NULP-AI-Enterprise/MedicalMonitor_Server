using FastEndpoints;
using PatientMonitoring.Api.Services.CentralStation;

namespace PatientMonitoring.Api.Endpoints.CentralStation;

public sealed class DeletePatientEndpoint(ICentralStationService centralStation)
    : EndpointWithoutRequest
{
    public override void Configure()
    {
        Delete("/api/v1/patients/{patientId}");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var patientId = Route<string>("patientId") ?? string.Empty;
        var deleted = await centralStation.DeletePatientAsync(patientId, ct);

        if (!deleted)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.NoContentAsync(ct);
    }
}
