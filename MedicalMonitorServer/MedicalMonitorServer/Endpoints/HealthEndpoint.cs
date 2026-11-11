using FastEndpoints;
using MedicalMonitorServer.Contracts.Api;
using MedicalMonitorServer.Core.Monitoring;
using MedicalMonitorServer.Services;

namespace MedicalMonitorServer.Endpoints;

public sealed class HealthEndpoint : EndpointWithoutRequest<HealthDto>
{
    private readonly IPatientRegistry _patients;
    private readonly ConnectionTracker _connections;

    public HealthEndpoint(IPatientRegistry patients, ConnectionTracker connections)
    {
        _patients = patients;
        _connections = connections;
    }

    public override void Configure()
    {
        Get("/api/v1/health");
        AllowAnonymous();
        Summary(s => s.Summary = "Service health, active patients and connected stations");
    }

    public override Task HandleAsync(CancellationToken ct) =>
        Send.OkAsync(new HealthDto("Healthy", DateTimeOffset.UtcNow, _patients.Count, _connections.Count), ct);
}
