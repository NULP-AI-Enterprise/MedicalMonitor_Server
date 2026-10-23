using FastEndpoints;
using Monitoring.Shared.DTOs;
using PatientMonitoring.Api.Services.CentralStation;

namespace PatientMonitoring.Api.Endpoints.CentralStation;

public sealed class UpdatePatientEndpoint(ICentralStationService centralStation)
    : Endpoint<PatientInfoDto, PatientInfoDto>
{
    public override void Configure()
    {
        Put("/api/v1/patients/{patientId}");
        AllowAnonymous();
    }

    public override async Task HandleAsync(PatientInfoDto req, CancellationToken ct)
    {
        var patientId = Route<string>("patientId") ?? req.PatientId;
        var updated = await centralStation.UpdatePatientAsync(patientId, req, ct);

        if (updated is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(updated, ct);
    }
}
