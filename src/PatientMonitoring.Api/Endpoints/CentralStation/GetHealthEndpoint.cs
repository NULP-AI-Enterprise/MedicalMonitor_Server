using FastEndpoints;
using PatientMonitoring.Api.Services.CentralStation;

namespace PatientMonitoring.Api.Endpoints.CentralStation;

public sealed record HealthCheckResponse(
    string Status,
    DateTimeOffset Timestamp,
    int ActivePatients,
    int ConnectedStations);

public sealed class GetHealthEndpoint(ICentralStationService centralStation)
    : EndpointWithoutRequest<HealthCheckResponse>
{
    public override void Configure()
    {
        Get("/api/v1/health");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var patients = await centralStation.GetPatientsAsync(ct);
        await Send.OkAsync(new HealthCheckResponse(
            Status: "Healthy",
            Timestamp: DateTimeOffset.UtcNow,
            ActivePatients: patients.Count,
            ConnectedStations: centralStation.ConnectedStationsCount
        ), ct);
    }
}
