using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Monitoring.BL.Services;

namespace PatientMonitoring.Api.Services.CentralStation;

/// <summary>
/// Background service driving the Central Nurse Station real-time vitals and waveform loop.
/// Emits ReceiveVitals ticks every 500 ms for all active monitored beds per API_CONTRACT.md.
/// </summary>
public sealed class CentralStationVitalsHostedService : BackgroundService
{
    private readonly ICentralStationService _centralStation;
    private readonly ILogger<CentralStationVitalsHostedService> _logger;

    public CentralStationVitalsHostedService(
        ICentralStationService centralStation,
        ILogger<CentralStationVitalsHostedService> logger)
    {
        _centralStation = centralStation;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Central Station vitals generator background service started (interval: {IntervalMs} ms).",
            GeneratorSettings.UpdateIntervalMs);

        using var timer = new PeriodicTimer(GeneratorSettings.UpdateInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await _centralStation.PublishTickAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error occurred during Central Station vitals tick.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }

        _logger.LogInformation("Central Station vitals generator background service stopped.");
    }
}
