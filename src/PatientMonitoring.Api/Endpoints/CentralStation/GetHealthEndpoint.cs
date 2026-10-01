using FastEndpoints;
using PatientMonitoring.Api.Services.CentralStation;

namespace PatientMonitoring.Api.Endpoints.CentralStation;

public sealed class GetHealthEndpoint(ICentralStationService centralStation)
    : EndpointWithoutRequest<object>
{
    public override void Configure()
    {
        Get("/api/v1/health");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var patients = await centralStation.GetPatientsAsync(ct);
        await Send.OkAsync(new
        {
            status = "Healthy",
            timestamp = DateTime.UtcNow,
            activePatients = patients.Count,
            connectedStations = centralStation.ConnectedStationsCount
        }, ct);
    }
}
