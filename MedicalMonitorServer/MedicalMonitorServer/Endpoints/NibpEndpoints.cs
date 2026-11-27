using FastEndpoints;
using MedicalMonitorServer.Contracts.Api;
using MedicalMonitorServer.Services;

namespace MedicalMonitorServer.Endpoints;

/// <summary>
/// Start or stop of a patient's cuff measurement, asked for by a station. Like every other
/// write it needs the operator key, or comes from the station on this machine (ApiKeyMiddleware).
/// </summary>
public abstract class NibpEndpoint : Endpoint<PatientRouteRequest, NibpRequestDto>
{
    private readonly NibpControl _nibp;

    protected NibpEndpoint(NibpControl nibp) => _nibp = nibp;

    /// <summary>True for the endpoint that starts a measurement, false for the one that stops it.</summary>
    protected abstract bool Start { get; }

    public override async Task HandleAsync(PatientRouteRequest req, CancellationToken ct)
    {
        var result = _nibp.Request(req.PatientId, Start, HttpContext.Connection.RemoteIpAddress?.ToString());

        switch (result.Outcome)
        {
            case NibpRequestOutcome.Accepted:
                // 202: the command is on its way; the monitor's next vitals say what it made of it.
                await Send.ResponseAsync(
                    new NibpRequestDto(req.PatientId, result.MonitorIp!, Start ? "start" : "stop"),
                    StatusCodes.Status202Accepted,
                    ct);
                return;

            case NibpRequestOutcome.PatientNotFound:
                await Send.NotFoundAsync(ct);
                return;

            default:
                AddError(result.Refusal!);
                await Send.ErrorsAsync(StatusCodes.Status409Conflict, ct);
                return;
        }
    }
}

public sealed class StartNibpEndpoint : NibpEndpoint
{
    public StartNibpEndpoint(NibpControl nibp) : base(nibp)
    {
    }

    protected override bool Start => true;

    public override void Configure()
    {
        Post("/api/v1/patients/{patientId}/nibp/start");
        AllowAnonymous();
        Summary(s =>
        {
            s.Summary = "Start a cuff (NIBP) measurement on the patient's monitor";
            s.Description =
                "Sends the start command to the monitor the patient is on, once. 202 means it was sent: " +
                "whether the cuff inflates shows in the vitals that follow (nibp.measuring, then the reading). " +
                "409 with a reason when the monitor cannot be told (see canStartNibp in GET /api/v1/monitors), " +
                "is not sending data, or is measuring already.";
        });
    }
}

public sealed class StopNibpEndpoint : NibpEndpoint
{
    public StopNibpEndpoint(NibpControl nibp) : base(nibp)
    {
    }

    protected override bool Start => false;

    public override void Configure()
    {
        Post("/api/v1/patients/{patientId}/nibp/stop");
        AllowAnonymous();
        Summary(s =>
        {
            s.Summary = "Stop the cuff (NIBP) measurement running on the patient's monitor";
            s.Description =
                "Sends the stop command to the monitor the patient is on, once; 202 means it was sent. " +
                "Stopping when nothing is running does no harm.";
        });
    }
}
