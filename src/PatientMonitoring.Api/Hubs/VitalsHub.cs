using Microsoft.AspNetCore.SignalR;
using Monitoring.Shared;
using Monitoring.Shared.DTOs;
using PatientMonitoring.Api.Services.CentralStation;

namespace PatientMonitoring.Api.Hubs;

/// <summary>
/// SignalR WebSocket Hub implementing the Central Nurse Station contract on /hubs/vitals.
/// </summary>
public class VitalsHub : Hub
{
    private readonly ICentralStationService _centralStation;
    private readonly ILogger<VitalsHub> _logger;

    public VitalsHub(ICentralStationService centralStation, ILogger<VitalsHub> logger)
    {
        _centralStation = centralStation;
        _logger = logger;
    }

    /// <summary>Returns the current patient roster.</summary>
    public async Task<PatientInfoDto[]> GetPatients()
    {
        var patients = await _centralStation.GetPatientsAsync();
        return patients.ToArray();
    }

    /// <summary>Returns currently active clinical alerts.</summary>
    public AlertDto[] GetActiveAlerts()
    {
        return _centralStation.GetActiveAlerts().ToArray();
    }

    /// <summary>Acknowledges (silences) an alert unit-wide.</summary>
    public async Task AcknowledgeAlert(string alertId)
    {
        var acknowledged = await _centralStation.AcknowledgeAlertAsync(alertId);
        if (acknowledged is null)
        {
            _logger.LogInformation("Acknowledge ignored — alert {AlertId} is no longer active.", alertId);
            return;
        }

        _logger.LogInformation("Alert {AlertId} acknowledged by station {ConnectionId}.", alertId, Context.ConnectionId);
    }

    public override async Task OnConnectedAsync()
    {
        _centralStation.IncrementConnectedStations();
        _logger.LogInformation("Central Station connected to /hubs/vitals: {ConnectionId}", Context.ConnectionId);

        var patients = await _centralStation.GetPatientsAsync();
        foreach (var patient in patients)
        {
            await Clients.Caller.SendAsync(HubMethods.PatientAdded, patient);
        }

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _centralStation.DecrementConnectedStations();

        if (exception is null)
            _logger.LogInformation("Central Station disconnected from /hubs/vitals: {ConnectionId}", Context.ConnectionId);
        else
            _logger.LogWarning(exception, "Central Station connection dropped from /hubs/vitals: {ConnectionId}", Context.ConnectionId);

        await base.OnDisconnectedAsync(exception);
    }
}
