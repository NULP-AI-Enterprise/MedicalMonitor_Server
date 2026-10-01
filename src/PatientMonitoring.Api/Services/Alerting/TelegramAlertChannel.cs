using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PatientMonitoring.Api.Contracts;
using PatientMonitoring.Api.Models;

namespace PatientMonitoring.Api.Services.Alerting;

public sealed class TelegramAlertOptions
{
    public const string SectionName = "AlertChannels:Telegram";

    public bool Enabled { get; set; } = false;

    public string? BotToken { get; set; }

    public string? ChatId { get; set; }

    public int TimeoutSeconds { get; set; } = 5;
}

public sealed class TelegramAlertChannel(
    IOptions<TelegramAlertOptions> options,
    HttpClient httpClient,
    ILogger<TelegramAlertChannel> logger) : IAlertChannel
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly TelegramAlertOptions _options = options.Value;

    public string Name => "Telegram";

    public bool IsEnabled => _options.Enabled &&
                             !string.IsNullOrWhiteSpace(_options.BotToken) &&
                             !string.IsNullOrWhiteSpace(_options.ChatId);

    public async Task SendAlertAsync(PatientAlertNotification alert, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(_options.BotToken) || string.IsNullOrWhiteSpace(_options.ChatId))
        {
            return;
        }

        try
        {
            var message = FormatMessage(alert);
            var payload = new
            {
                chat_id = _options.ChatId,
                text = message,
                parse_mode = "HTML"
            };

            var json = JsonSerializer.Serialize(payload, JsonOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            var url = $"https://api.telegram.org/bot{_options.BotToken}/sendMessage";

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_options.TimeoutSeconds));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            var response = await httpClient.PostAsync(url, content, linkedCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                var responseBody = await response.Content.ReadAsStringAsync(linkedCts.Token);
                logger.LogWarning("Telegram alert failed with status {StatusCode}: {Response}", response.StatusCode, responseBody);
            }
            else
            {
                logger.LogInformation("Telegram alert successfully sent for patient {PatientId} ({Status})", alert.PatientId, alert.Status);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to send Telegram alert for patient {PatientId}", alert.PatientId);
        }
    }

    private static string FormatMessage(PatientAlertNotification alert)
    {
        var icon = alert.Status switch
        {
            VitalStatus.Critical => "🚨",
            VitalStatus.Warning => "⚠️",
            _ => "ℹ️"
        };

        var title = alert.Status switch
        {
            VitalStatus.Critical => "КРИТИЧНИЙ СТАН ПАЦІЄНТА",
            VitalStatus.Warning => "ПОПЕРЕДЖЕННЯ: ЗМІНА ПОКАЗНИКІВ",
            _ => "НОРМАЛІЗАЦІЯ ПОКАЗНИКІВ"
        };

        var sb = new StringBuilder();
        sb.AppendLine($"{icon} <b>{title}</b>");
        sb.AppendLine($"Пацієнт: <b>{EscapeHtml(alert.PatientFullName)}</b>");
        sb.AppendLine($"ID: <code>{alert.PatientId}</code>");
        sb.AppendLine($"Час: <code>{alert.RecordedAt:yyyy-MM-dd HH:mm:ss} UTC</code>");

        if (alert.Alerts.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("<b>Відхилення від норми:</b>");
            foreach (var item in alert.Alerts)
            {
                sb.AppendLine($"• <b>{EscapeHtml(item.Parameter)}</b>: {item.Value} ({EscapeHtml(item.Message)})");
            }
        }

        return sb.ToString();
    }

    private static string EscapeHtml(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;");
    }
}
