using Microsoft.AspNetCore.SignalR;
using PatientMonitoring.Api.Services;

namespace PatientMonitoring.Api.Hubs;

/// <summary>
/// Хаб для отримання даних у реальному часі. Клієнт підписується або на конкретних пацієнтів
/// (SubscribeToPatient), або на всіх одразу (SubscribeToAll). Не варто робити обидві підписки
/// одночасно — повідомлення прийдуть двічі.
/// </summary>
public sealed class PatientMonitoringHub(IPatientService patients, ILogger<PatientMonitoringHub> logger)
    : Hub<IPatientMonitoringClient>
{
    public const string AllPatientsGroup = "patients:all";

    public static string PatientGroup(Guid patientId) => $"patient:{patientId}";

    public async Task SubscribeToPatient(Guid patientId)
    {
        if (!await patients.ExistsAsync(patientId, Context.ConnectionAborted))
        {
            throw new HubException($"Patient '{patientId}' was not found.");
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, PatientGroup(patientId), Context.ConnectionAborted);
        logger.LogDebug("Connection {ConnectionId} subscribed to patient {PatientId}", Context.ConnectionId, patientId);
    }

    public Task UnsubscribeFromPatient(Guid patientId)
        => Groups.RemoveFromGroupAsync(Context.ConnectionId, PatientGroup(patientId), Context.ConnectionAborted);

    public async Task SubscribeToAll()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, AllPatientsGroup, Context.ConnectionAborted);
        logger.LogDebug("Connection {ConnectionId} subscribed to all patients", Context.ConnectionId);
    }

    public Task UnsubscribeFromAll()
        => Groups.RemoveFromGroupAsync(Context.ConnectionId, AllPatientsGroup, Context.ConnectionAborted);

    public override Task OnConnectedAsync()
    {
        logger.LogInformation("Client connected: {ConnectionId}", Context.ConnectionId);
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        logger.LogInformation("Client disconnected: {ConnectionId}", Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}
