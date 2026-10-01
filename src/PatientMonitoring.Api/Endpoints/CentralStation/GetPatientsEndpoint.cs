using FastEndpoints;
using Monitoring.Shared.DTOs;
using PatientMonitoring.Api.Services.CentralStation;

namespace PatientMonitoring.Api.Endpoints.CentralStation;

public sealed class GetPatientsEndpoint(ICentralStationService centralStation)
    : EndpointWithoutRequest<IReadOnlyList<PatientInfoDto>>
{
    public override void Configure()
    {
        Get("/api/v1/patients");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var patients = await centralStation.GetPatientsAsync(ct);
        await Send.OkAsync(patients, ct);
    }
}
