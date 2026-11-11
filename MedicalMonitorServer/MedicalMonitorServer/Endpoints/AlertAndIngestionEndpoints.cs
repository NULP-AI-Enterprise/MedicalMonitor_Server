using FastEndpoints;
using MedicalMonitorServer.Contracts.Api;
using MedicalMonitorServer.Core.Monitoring;
using MedicalMonitorServer.Services;

namespace MedicalMonitorServer.Endpoints;

public sealed class AlertRouteRequest
{
    public string AlertId { get; set; } = string.Empty;
}

public sealed class GetActiveAlertsEndpoint : EndpointWithoutRequest<List<AlertDto>>
{
    private readonly IAlertStateService _alerts;

    public GetActiveAlertsEndpoint(IAlertStateService alerts) => _alerts = alerts;

    public override void Configure()
    {
        Get("/api/v1/alerts/active");
        AllowAnonymous();
        Summary(s => s.Summary = "All active alerts");
    }

    public override Task HandleAsync(CancellationToken ct) =>
        Send.OkAsync(_alerts.GetActiveAlerts().ToList(), ct);
}

public sealed class AcknowledgeAlertEndpoint : Endpoint<AlertRouteRequest, AlertDto>
{
    private readonly IMonitorPipeline _pipeline;

    public AcknowledgeAlertEndpoint(IMonitorPipeline pipeline) => _pipeline = pipeline;

    public override void Configure()
    {
        Post("/api/v1/alerts/{alertId}/acknowledge");
        AllowAnonymous();
        Summary(s => s.Summary = "Silence an alert on every station");
    }

    public override async Task HandleAsync(AlertRouteRequest req, CancellationToken ct)
    {
        var acknowledged = await _pipeline.AcknowledgeAlertAsync(req.AlertId, ct);
        if (acknowledged is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(acknowledged, ct);
    }
}

public sealed class IngestTelemetryEndpoint : Endpoint<MonitorUpdateDto>
{
    private readonly IMonitorPipeline _pipeline;

    public IngestTelemetryEndpoint(IMonitorPipeline pipeline) => _pipeline = pipeline;

    public override void Configure()
    {
        Post("/api/v1/ingestion/telemetry");
        AllowAnonymous();
        Summary(s => s.Summary = "Accept a telemetry frame from a monitor gateway");
    }

    public override async Task HandleAsync(MonitorUpdateDto req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.PatientId) || req.Vitals is null)
        {
            AddError("patientId and vitals are required.");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        await _pipeline.IngestTelemetryAsync(req, ct);
        await Send.ResponseAsync(null, StatusCodes.Status202Accepted, ct);
    }
}

public sealed class IngestAlertEndpoint : Endpoint<AlertDto>
{
    private readonly IMonitorPipeline _pipeline;

    public IngestAlertEndpoint(IMonitorPipeline pipeline) => _pipeline = pipeline;

    public override void Configure()
    {
        Post("/api/v1/ingestion/alerts");
        AllowAnonymous();
        Summary(s => s.Summary = "Accept a hardware alert from a monitor gateway");
    }

    public override async Task HandleAsync(AlertDto req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.PatientId))
        {
            AddError("patientId is required.");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        // The alert key is always derived server-side so clients cannot collide with each other.
        var alert = req with { AlertId = AlertDto.BuildId(req.PatientId, req.VitalSign) };
        await _pipeline.IngestAlertAsync(alert, ct);
        await Send.ResponseAsync(null, StatusCodes.Status202Accepted, ct);
    }
}
