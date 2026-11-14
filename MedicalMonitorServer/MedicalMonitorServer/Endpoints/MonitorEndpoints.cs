using System.Net;
using FastEndpoints;
using MedicalMonitorServer.Contracts.Api;
using MedicalMonitorServer.Core.Monitoring;
using MedicalMonitorServer.Services;

namespace MedicalMonitorServer.Endpoints;

/// <summary>POST body plus the patient id from the route.</summary>
public sealed class TransferPatientRequest
{
    public string PatientId { get; set; } = string.Empty;

    /// <summary>IP address of the monitor the patient is now on (see GET /api/v1/monitors).</summary>
    public string MonitorIp { get; set; } = string.Empty;
}

public sealed class GetMonitorsEndpoint : EndpointWithoutRequest<List<MonitorBindingDto>>
{
    private readonly IMonitorBindings _bindings;

    public GetMonitorsEndpoint(IMonitorBindings bindings) => _bindings = bindings;

    public override void Configure()
    {
        Get("/api/v1/monitors");
        AllowAnonymous();
        Summary(s => s.Summary = "Monitors that have sent data and the patient each one feeds");
    }

    public override Task HandleAsync(CancellationToken ct) =>
        Send.OkAsync(_bindings.GetAll().ToList(), ct);
}

public sealed class TransferPatientEndpoint : Endpoint<TransferPatientRequest, TransferResultDto>
{
    private readonly MonitorRouter _router;

    public TransferPatientEndpoint(MonitorRouter router) => _router = router;

    public override void Configure()
    {
        Post("/api/v1/patients/{patientId}/transfer");
        AllowAnonymous();
        Summary(s =>
        {
            s.Summary = "Move a patient to another monitor";
            s.Description =
                "Binds the patient to the given monitor and releases the one they were on. " +
                "Their vitals snapshot and alerts are reset and the bed follows the monitor. " +
                "An anonymous entry on the target monitor is replaced; an identified patient " +
                "bound there stays on the roster and is returned as displacedPatientId.";
        });
    }

    public override async Task HandleAsync(TransferPatientRequest req, CancellationToken ct)
    {
        if (!IPAddress.TryParse(req.MonitorIp, out var address))
        {
            AddError(r => r.MonitorIp, "monitorIp must be the IP address of a monitor.");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        var outcome = await _router.TransferAsync(req.PatientId, address.ToString(), ct);
        switch (outcome.Kind)
        {
            case TransferOutcomeKind.PatientNotFound:
                await Send.NotFoundAsync(ct);
                return;

            case TransferOutcomeKind.MonitorNotFound:
                AddError(r => r.MonitorIp, "No monitor with this address has sent data.");
                await Send.ErrorsAsync(StatusCodes.Status404NotFound, ct);
                return;

            default:
                await Send.OkAsync(outcome.Result!, ct);
                return;
        }
    }
}
