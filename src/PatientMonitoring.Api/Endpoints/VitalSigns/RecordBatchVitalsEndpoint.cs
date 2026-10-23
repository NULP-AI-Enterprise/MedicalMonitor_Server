using FastEndpoints;
using Microsoft.AspNetCore.Http;
using PatientMonitoring.Api.Contracts;
using PatientMonitoring.Api.Services;

namespace PatientMonitoring.Api.Endpoints.VitalSigns;

public sealed class RecordBatchVitalsEndpoint(IVitalSignsService vitals)
    : Endpoint<RecordVitalSignsBatchRequest, RecordVitalSignsBatchResponse>
{
    public override void Configure()
    {
        Post("/api/patients/{patientId:guid}/vitals/batch");
        AllowAnonymous();
    }

    public override async Task HandleAsync(RecordVitalSignsBatchRequest req, CancellationToken ct)
    {
        var patientId = Route<Guid>("patientId");
        var result = await vitals.RecordBatchAsync(patientId, req, ct);

        if (result is null)
        {
            await Send.ResultAsync(TypedResults.Json(
                new { error = $"Patient '{patientId}' not found." },
                statusCode: StatusCodes.Status404NotFound));
            return;
        }

        await Send.OkAsync(result, ct);
    }
}
