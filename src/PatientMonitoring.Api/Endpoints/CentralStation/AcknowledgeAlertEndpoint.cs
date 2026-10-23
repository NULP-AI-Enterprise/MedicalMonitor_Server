using FastEndpoints;
using Monitoring.Shared.DTOs;
using PatientMonitoring.Api.Services.CentralStation;

namespace PatientMonitoring.Api.Endpoints.CentralStation;

public sealed class AcknowledgeAlertEndpoint(ICentralStationService centralStation)
    : EndpointWithoutRequest<AlertDto>
{
    public override void Configure()
    {
        Post("/api/v1/alerts/{alertId}/acknowledge");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var alertId = Route<string>("alertId") ?? string.Empty;
        var acknowledged = await centralStation.AcknowledgeAlertAsync(alertId);

        if (acknowledged is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(acknowledged, ct);
    }
}
