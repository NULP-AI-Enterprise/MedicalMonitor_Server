using FastEndpoints;
using Monitoring.Shared.DTOs;
using PatientMonitoring.Api.Services.CentralStation;

namespace PatientMonitoring.Api.Endpoints.CentralStation;

public sealed class GetPatientByIdEndpoint(ICentralStationService centralStation)
    : EndpointWithoutRequest<PatientInfoDto>
{
    public override void Configure()
    {
        Get("/api/v1/patients/{patientId}");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var patientId = Route<string>("patientId") ?? string.Empty;
        var patient = await centralStation.GetPatientAsync(patientId, ct);

        if (patient is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(patient, ct);
    }
}
