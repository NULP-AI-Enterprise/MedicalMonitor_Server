using FastEndpoints;
using Microsoft.AspNetCore.Http;
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
        if (string.IsNullOrWhiteSpace(patientId))
        {
            ThrowError("patientId is required.", StatusCodes.Status400BadRequest);
        }

        if (req.BedNumber < 1)
        {
            ThrowError("bedNumber must be 1 or greater.", StatusCodes.Status400BadRequest);
        }

        if (!CentralStationRules.IsValidPatientType(req.PatientType))
        {
            ThrowError("patientType must be one of: Adu, Ped, Neo.", StatusCodes.Status400BadRequest);
        }

        try
        {
            var updated = await centralStation.UpdatePatientAsync(patientId, req, ct);
            if (updated is null)
            {
                await Send.NotFoundAsync(ct);
                return;
            }

            await Send.OkAsync(updated, ct);
        }
        catch (CentralStationConflictException ex)
        {
            await Send.ResultAsync(TypedResults.Conflict(new { error = ex.Message }));
        }
    }
}
