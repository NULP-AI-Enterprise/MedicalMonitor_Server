using FastEndpoints;
using MedicalMonitorServer.Contracts.Api;
using MedicalMonitorServer.Contracts.Models;
using MedicalMonitorServer.Core.Monitoring;
using MedicalMonitorServer.DeviceDriver;
using MedicalMonitorServer.Services;

namespace MedicalMonitorServer.Endpoints;

public sealed class HealthEndpoint : EndpointWithoutRequest<HealthDto>
{
    private readonly IPatientRegistry _patients;
    private readonly ConnectionTracker _connections;
    private readonly DeviceService _devices;
    private readonly FeedStatus _feed;
    private readonly PatientHistory _history;
    private readonly TimeSpan _staleAfter;

    public HealthEndpoint(
        IPatientRegistry patients,
        ConnectionTracker connections,
        DeviceService devices,
        FeedStatus feed,
        PatientHistory history,
        IConfiguration configuration)
    {
        _patients = patients;
        _connections = connections;
        _devices = devices;
        _feed = feed;
        _history = history;
        _staleAfter = TimeSpan.FromSeconds(configuration.GetValue("Health:StaleAfterSeconds", 30));
    }

    public override void Configure()
    {
        Get("/api/v1/health");
        AllowAnonymous();
        Summary(s => s.Summary = "Service health: monitor links, data freshness, patients and connected stations");
    }

    public override Task HandleAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var monitors = ConnectedMonitors();
        var lastData = _feed.LastDataAt;
        double? age = lastData is { } at ? Math.Round((now - at).TotalSeconds, 1) : null;
        var fresh = lastData is { } t && now - t <= _staleAfter;

        var status = fresh ? "Healthy"
            : _patients.Count > 0 && monitors == 0 ? "Unhealthy"
            : "Degraded";

        // The history is said beside the status, not folded into it: the status is about the
        // monitors, and a station must tell "no data" from "data, but none of it is kept".
        var dto = new HealthDto(status, now, _patients.Count, _connections.Count, monitors, lastData, age, _history.Status);
        // Always 200: the stations poll this to tell "server reachable" apart from "monitors silent".
        return Send.OkAsync(dto, ct);
    }

    private int ConnectedMonitors()
    {
        try
        {
            return _devices.GetAllDevice().Count(d => d.State == DeviceStateEnum.Connected);
        }
        catch (InvalidOperationException)
        {
            // The device list is mutated by the watchdog thread; a concurrent change is not a health failure.
            return 0;
        }
    }
}
