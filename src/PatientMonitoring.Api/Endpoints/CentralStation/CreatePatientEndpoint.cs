using FastEndpoints;
using Monitoring.Shared.DTOs;
using PatientMonitoring.Api.Services.CentralStation;

namespace PatientMonitoring.Api.Endpoints.CentralStation;

public sealed class CreatePatientEndpoint(ICentralStationService centralStation)
    : Endpoint<PatientInfoDto, PatientInfoDto>
{
    public override void Configure()
    {
        Post("/api/v1/patients");
        AllowAnonymous();
    }

    public override async Task HandleAsync(PatientInfoDto req, CancellationToken ct)
    {
        var created = await centralStation.CreatePatientAsync(req, ct);
        await Send.CreatedAtAsync<GetPatientByIdEndpoint>(
            routeValues: new { patientId = created.PatientId },
            responseBody: created,
            cancellation: ct);
    }
}
