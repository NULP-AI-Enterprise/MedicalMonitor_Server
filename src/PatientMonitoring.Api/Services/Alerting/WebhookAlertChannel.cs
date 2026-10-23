using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PatientMonitoring.Api.Contracts;

namespace PatientMonitoring.Api.Services.Alerting;

public sealed class WebhookAlertOptions
{
    public const string SectionName = "AlertChannels:Webhook";

    public bool Enabled { get; set; } = false;

    public string? Url { get; set; }

    public int TimeoutSeconds { get; set; } = 5;
}

public sealed class WebhookAlertChannel(
    IOptions<WebhookAlertOptions> options,
    HttpClient httpClient,
    ILogger<WebhookAlertChannel> logger) : IAlertChannel
{
    private readonly WebhookAlertOptions _options = options.Value;

    public string Name => "Webhook";

    public bool IsEnabled => _options.Enabled && !string.IsNullOrWhiteSpace(_options.Url);

    public async Task SendAlertAsync(PatientAlertNotification alert, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(_options.Url))
        {
            return;
        }

        try
        {
            var json = JsonSerializer.Serialize(alert);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_options.TimeoutSeconds));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            var response = await httpClient.PostAsync(_options.Url, content, linkedCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Webhook alert to {Url} returned status code {StatusCode}", _options.Url, response.StatusCode);
            }
            else
            {
                logger.LogInformation("Webhook alert successfully delivered for patient {PatientId} ({Status})", alert.PatientId, alert.Status);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to deliver webhook alert for patient {PatientId} to {Url}", alert.PatientId, _options.Url);
        }
    }
}
