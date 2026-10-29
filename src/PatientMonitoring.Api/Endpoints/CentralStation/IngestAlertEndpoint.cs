using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Monitoring.Shared.DTOs;
using PatientMonitoring.Api.Services.CentralStation;

namespace PatientMonitoring.Api.Endpoints.CentralStation;

public sealed class IngestAlertEndpoint(ICentralStationService centralStation)
    : Endpoint<AlertDto>
{
    public override void Configure()
    {
        Post("/api/v1/ingestion/alerts");
        AllowAnonymous();
    }

    public override async Task HandleAsync(AlertDto req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.PatientId))
        {
            ThrowError("patientId is required.", StatusCodes.Status400BadRequest);
        }

        if (!await centralStation.ProcessAlertAsync(req, ct))
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.StatusCodeAsync(StatusCodes.Status202Accepted, ct);
    }
}
