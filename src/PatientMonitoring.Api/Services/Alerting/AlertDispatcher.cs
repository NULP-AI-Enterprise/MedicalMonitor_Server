using PatientMonitoring.Api.Contracts;

namespace PatientMonitoring.Api.Services.Alerting;

public sealed class AlertDispatcher(
    IEnumerable<IAlertChannel> channels,
    ILogger<AlertDispatcher> logger) : IAlertDispatcher
{
    public async Task DispatchAsync(PatientAlertNotification alert, CancellationToken cancellationToken = default)
    {
        var activeChannels = channels.Where(c => c.IsEnabled).ToList();
        if (activeChannels.Count == 0)
        {
            logger.LogDebug("No active external alert channels registered for patient {PatientId}", alert.PatientId);
            return;
        }

        logger.LogInformation("Dispatching alert for patient {PatientId} ({Status}) to {Count} external channels",
            alert.PatientId, alert.Status, activeChannels.Count);

        var tasks = activeChannels.Select(c => SendChannelSafelyAsync(c, alert, cancellationToken));
        await Task.WhenAll(tasks);
    }

    private async Task SendChannelSafelyAsync(IAlertChannel channel, PatientAlertNotification alert, CancellationToken cancellationToken)
    {
        try
        {
            await channel.SendAlertAsync(alert, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error while dispatching alert to channel {ChannelName}", channel.Name);
        }
    }
}
