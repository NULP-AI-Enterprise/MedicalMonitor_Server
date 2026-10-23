using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PatientMonitoring.Api.Contracts;

namespace PatientMonitoring.Api.Services.Relay;

public sealed class DataRelayOptions
{
    public const string SectionName = "DataRelay";

    public bool Enabled { get; set; } = false;

    public string? TargetUrl { get; set; }

    public string? ApiKey { get; set; }

    public string HeaderName { get; set; } = "X-API-KEY";

    public int TimeoutSeconds { get; set; } = 5;
}

public sealed class HttpDataRelay(
    IOptions<DataRelayOptions> options,
    HttpClient httpClient,
    ILogger<HttpDataRelay> logger) : IRealtimeDataRelay
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly DataRelayOptions _options = options.Value;

    public bool IsEnabled => _options.Enabled && !string.IsNullOrWhiteSpace(_options.TargetUrl);

    public async Task RelayVitalSignsAsync(VitalSignsResponse vitals, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(_options.TargetUrl))
        {
            return;
        }

        try
        {
            var envelope = new
            {
                eventType = "vital_signs_recorded",
                timestamp = DateTime.UtcNow,
                data = vitals
            };

            await SendPayloadAsync(_options.TargetUrl, envelope, cancellationToken);
            logger.LogDebug("Relayed vitals for patient {PatientId} to external application at {Url}", vitals.PatientId, _options.TargetUrl);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to relay vital signs for patient {PatientId} to external app {Url}", vitals.PatientId, _options.TargetUrl);
        }
    }

    public async Task RelayBatchAsync(RecordVitalSignsBatchResponse batch, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(_options.TargetUrl))
        {
            return;
        }

        try
        {
            var envelope = new
            {
                eventType = "vital_signs_batch_recorded",
                timestamp = DateTime.UtcNow,
                data = batch
            };

            await SendPayloadAsync(_options.TargetUrl, envelope, cancellationToken);
            logger.LogDebug("Relayed vitals batch ({Count} items) for patient {PatientId} to external application", batch.InsertedCount, batch.PatientId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to relay vitals batch for patient {PatientId} to external app {Url}", batch.PatientId, _options.TargetUrl);
        }
    }

    private async Task SendPayloadAsync(string url, object payload, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = content
        };

        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            request.Headers.TryAddWithoutValidation(_options.HeaderName, _options.ApiKey);
        }

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_options.TimeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var response = await httpClient.SendAsync(request, linkedCts.Token);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("External application relay to {Url} responded with HTTP {StatusCode}", url, response.StatusCode);
        }
    }
}
