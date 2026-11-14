using MedicalMonitorServer.Contracts.Api;
using MedicalMonitorServer.Core.Monitoring;
using MedicalMonitorServer.Security;
using MedicalMonitorServer.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace MedicalMonitorServer.Hubs;

/// <summary>
/// Real-time endpoint for nurse stations (route <see cref="HubMethods.Route"/>).
/// Clients call GetPatients / GetActiveAlerts on connect, then follow the pushed
/// ReceiveVitals / Patient* / Alert* messages. Reading is open; AcknowledgeAlert needs the
/// operator key, sent as the X-Api-Key header or the access_token query parameter
/// (browsers cannot set headers on WebSocket connections).
/// </summary>
public class VitalsHub : Hub
{
    private readonly IPatientRegistry _patients;
    private readonly IAlertStateService _alerts;
    private readonly IMonitorPipeline _pipeline;
    private readonly ConnectionTracker _connections;
    private readonly ILogger<VitalsHub> _logger;
    private readonly ApiKeyOptions _keys;

    private const string OperatorItem = "IsOperator";

    public VitalsHub(
        IPatientRegistry patients,
        IAlertStateService alerts,
        IMonitorPipeline pipeline,
        ConnectionTracker connections,
        IOptions<ApiKeyOptions> keys,
        ILogger<VitalsHub> logger)
    {
        _patients = patients;
        _alerts = alerts;
        _pipeline = pipeline;
        _connections = connections;
        _logger = logger;
        _keys = keys.Value;
    }

    public PatientInfoDto[] GetPatients() => _patients.GetAll().ToArray();

    public AlertDto[] GetActiveAlerts() => _alerts.GetActiveAlerts().ToArray();

    /// <summary>Silences an alert for every connected station.</summary>
    public async Task AcknowledgeAlert(string alertId)
    {
        if (Context.Items[OperatorItem] is not true)
        {
            _logger.LogWarning("Acknowledge of {AlertId} rejected: connection {ConnectionId} has no operator key.",
                alertId, Context.ConnectionId);
            throw new HubException("Unauthorized: acknowledging alerts requires the operator API key.");
        }

        var acknowledged = await _pipeline.AcknowledgeAlertAsync(alertId);
        if (acknowledged is null)
        {
            _logger.LogInformation("Acknowledge ignored: alert {AlertId} is not active.", alertId);
        }
    }

    public override async Task OnConnectedAsync()
    {
        var http = Context.GetHttpContext();
        var key = http?.Request.Headers[ApiKeyOptions.HeaderName].ToString();
        if (string.IsNullOrEmpty(key)) key = http?.Request.Query["access_token"].ToString();
        Context.Items[OperatorItem] = ApiKeyOptions.Matches(key, _keys.OperatorKey);

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
