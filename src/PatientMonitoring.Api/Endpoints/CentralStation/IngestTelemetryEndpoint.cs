using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Monitoring.Shared.DTOs;
using PatientMonitoring.Api.Services.CentralStation;

namespace PatientMonitoring.Api.Endpoints.CentralStation;

public sealed class IngestTelemetryEndpoint(ICentralStationService centralStation)
    : Endpoint<MonitorUpdateDto>
{
    public override void Configure()
    {
        Post("/api/v1/ingestion/telemetry");
        AllowAnonymous();
    }

    public override async Task HandleAsync(MonitorUpdateDto req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.PatientId))
        {
            ThrowError("patientId is required.", StatusCodes.Status400BadRequest);
        }

        if (req.Vitals?.Nibp is null)
        {
            ThrowError("vitals and vitals.nibp are required.", StatusCodes.Status400BadRequest);
        }

        await centralStation.ProcessTelemetryAsync(req, ct);
        await Send.StatusCodeAsync(StatusCodes.Status202Accepted, ct);
    }
}
