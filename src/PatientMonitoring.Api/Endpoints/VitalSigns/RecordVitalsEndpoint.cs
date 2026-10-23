using FastEndpoints;
using Microsoft.AspNetCore.Http;
using PatientMonitoring.Api.Contracts;
using PatientMonitoring.Api.Services;

namespace PatientMonitoring.Api.Endpoints.VitalSigns;

public sealed class RecordVitalsEndpoint(IVitalSignsService vitals)
    : Endpoint<RecordVitalSignsRequest, VitalSignsResponse>
{
    public override void Configure()
    {
        Post("/api/patients/{patientId:guid}/vitals");
        AllowAnonymous();
    }

    public override async Task HandleAsync(RecordVitalSignsRequest req, CancellationToken ct)
    {
        var patientId = Route<Guid>("patientId");
        var result = await vitals.RecordAsync(patientId, req, ct);

        if (result is null)
        {
            await Send.ResultAsync(TypedResults.Json(
                new { error = $"Patient '{patientId}' not found." },
                statusCode: StatusCodes.Status404NotFound));
            return;
        }

        await Send.CreatedAtAsync<GetLatestPatientVitalsEndpoint>(
            routeValues: new { patientId },
            responseBody: result,
            cancellation: ct);
    }
}
