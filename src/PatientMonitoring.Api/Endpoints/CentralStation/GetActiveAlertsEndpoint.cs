using FastEndpoints;
using Monitoring.Shared.DTOs;
using PatientMonitoring.Api.Services.CentralStation;

namespace PatientMonitoring.Api.Endpoints.CentralStation;

public sealed class GetActiveAlertsEndpoint(ICentralStationService centralStation)
    : EndpointWithoutRequest<IReadOnlyList<AlertDto>>
{
    public override void Configure()
    {
        Get("/api/v1/alerts/active");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var alerts = centralStation.GetActiveAlerts();
        await Send.OkAsync(alerts, ct);
    }
}
