using FastEndpoints;
using MedicalMonitorServer.Contracts.Api;
using MedicalMonitorServer.Core.Monitoring;
using MedicalMonitorServer.Services;

namespace MedicalMonitorServer.Endpoints;

public sealed class AlertRouteRequest
{
    public string AlertId { get; set; } = string.Empty;
}

/// <summary>
/// Telemetry sanity checks. null (preferred) or 0 mean "not measured" and are accepted (both are
/// stored as null, so they never raise an alert); negative or physiologically impossible values are rejected.
/// </summary>
internal static class TelemetryValidation
{
    public static string? Validate(MonitorUpdateDto req)
    {
        if (string.IsNullOrWhiteSpace(req.PatientId)) return "patientId is required.";
        if (req.Vitals is null) return "vitals are required.";
        if (string.IsNullOrWhiteSpace(req.PatientName)) return "patientName is required.";
        if (req.BedNumber < 1) return "bedNumber must be 1 or greater.";

        var v = req.Vitals;
        return Range("heartRate", v.HeartRate, 300)
            ?? Range("spO2", v.SpO2, 100)
            ?? Range("nibp.systolic", v.Nibp?.Systolic, 300)
            ?? Range("nibp.diastolic", v.Nibp?.Diastolic, 250)
            ?? Range("nibp.meanArterialPressure", v.Nibp?.MeanArterialPressure, 300)
            ?? Range("respiratoryRate", v.RespiratoryRate, 150)
            ?? Range("pulseRate", v.PulseRate, 300)
            ?? (v.Temperature is null or 0 or (>= 20 and <= 45)
                ? null
                : "temperature must be null (not measured) or between 20 and 45 °C.")
            ?? (v.Nibp is { Systolic: > 0, Diastolic: > 0 } bp && bp.Diastolic >= bp.Systolic
                ? "nibp.diastolic must be lower than nibp.systolic."
                : null);
    }

    private static string? Range(string name, int? value, int max) =>
        value is null || (value >= 0 && value <= max) ? null : $"{name} must be null (not measured) or between 1 and {max}.";
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
        var error = TelemetryValidation.Validate(req);
        if (error is not null)
        {
            AddError(error);
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        // A gateway without a clock sync may omit the timestamp; never store year 0001.
        if (req.Timestamp == default) req = req with { Timestamp = DateTimeOffset.UtcNow };

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
        Summary(s => s.Summary = "Accept a hardware alert from a monitor gateway (needs a device code or a vital sign)");
    }

    public override async Task HandleAsync(AlertDto req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.PatientId))
        {
            AddError("patientId is required.");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        if (req.Code is null && req.VitalSign is null)
        {
            AddError("code or vitalSign is required.");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        // The alert key is always derived server-side so clients cannot collide with each other.
        // Coded device alarms get their own key; vital-sign alerts share the threshold key as before.
        var alert = req with
        {
            AlertId = req.Code is int code
                ? AlertDto.BuildDeviceId(req.PatientId, code)
                : AlertDto.BuildId(req.PatientId, req.VitalSign!.Value),
            Source = AlertSource.Device,
            Severity = req.Level is null ? req.Severity : AlertDto.SeverityOf(req.Level),
            Timestamp = req.Timestamp == default ? DateTimeOffset.UtcNow : req.Timestamp,
        };
        await _pipeline.IngestAlertAsync(alert, ct);
        await Send.ResponseAsync(null, StatusCodes.Status202Accepted, ct);
    }
}
