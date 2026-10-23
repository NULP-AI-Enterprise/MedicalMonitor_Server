using FastEndpoints;
using Monitoring.Shared.DTOs;
using PatientMonitoring.Api.Services.CentralStation;

namespace PatientMonitoring.Api.Endpoints.CentralStation;

public sealed class GetLatestVitalsEndpoint(ICentralStationService centralStation)
    : EndpointWithoutRequest<VitalsDto>
{
    public override void Configure()
    {
        Get("/api/v1/patients/{patientId}/vitals/latest");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var patientId = Route<string>("patientId") ?? string.Empty;
        var vitals = centralStation.GetLatestVitals(patientId);

        if (vitals is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(vitals, ct);
    }
}
