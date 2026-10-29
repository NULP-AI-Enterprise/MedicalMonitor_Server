using FastEndpoints;
using Microsoft.AspNetCore.Http;
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
        if (string.IsNullOrWhiteSpace(req.PatientId) || string.IsNullOrWhiteSpace(req.PatientName))
        {
            ThrowError("patientId and patientName are required fields.", StatusCodes.Status400BadRequest);
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
            var created = await centralStation.CreatePatientAsync(req, ct);
            await Send.CreatedAtAsync<GetPatientByIdEndpoint>(
                routeValues: new { patientId = created.PatientId },
                responseBody: created,
                cancellation: ct);
        }
        catch (CentralStationConflictException ex)
        {
            await Send.ResultAsync(TypedResults.Conflict(new { error = ex.Message }));
        }
    }
}
