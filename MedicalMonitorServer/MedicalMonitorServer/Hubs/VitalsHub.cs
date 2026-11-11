using MedicalMonitorServer.Contracts.Api;
using MedicalMonitorServer.Core.Monitoring;
using MedicalMonitorServer.Services;
using Microsoft.AspNetCore.SignalR;

namespace MedicalMonitorServer.Hubs;

/// <summary>
/// Real-time endpoint for nurse stations (route <see cref="HubMethods.Route"/>).
/// Clients call GetPatients / GetActiveAlerts on connect, then follow the pushed
/// ReceiveVitals / Patient* / Alert* messages.
/// </summary>
public class VitalsHub : Hub
{
    private readonly IPatientRegistry _patients;
    private readonly IAlertStateService _alerts;
    private readonly IMonitorPipeline _pipeline;
    private readonly ConnectionTracker _connections;
    private readonly ILogger<VitalsHub> _logger;

    public VitalsHub(
        IPatientRegistry patients,
        IAlertStateService alerts,
        IMonitorPipeline pipeline,
        ConnectionTracker connections,
        ILogger<VitalsHub> logger)
    {
        _patients = patients;
        _alerts = alerts;
        _pipeline = pipeline;
        _connections = connections;
        _logger = logger;
    }

    public PatientInfoDto[] GetPatients() => _patients.GetAll().ToArray();

    public AlertDto[] GetActiveAlerts() => _alerts.GetActiveAlerts().ToArray();

    /// <summary>Silences an alert for every connected station.</summary>
    public async Task AcknowledgeAlert(string alertId)
    {
        var acknowledged = await _pipeline.AcknowledgeAlertAsync(alertId);
        if (acknowledged is null)
        {
            _logger.LogInformation("Acknowledge ignored: alert {AlertId} is not active.", alertId);
        }
    }

    public override async Task OnConnectedAsync()
    {
        _connections.Increment();
        _logger.LogInformation("Station connected: {ConnectionId}", Context.ConnectionId);

        // Announce the roster so the client can lay out its beds before the first frame.
        foreach (var patient in _patients.GetAll())
        {
            await Clients.Caller.SendAsync(HubMethods.PatientAdded, patient);
        }

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _connections.Decrement();
        _logger.LogInformation("Station disconnected: {ConnectionId}", Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
}
