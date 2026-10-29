namespace Monitoring.BL.Services;

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Monitoring.Shared;

/// <summary>
/// Drives the simulation: every tick it generates a monitor update per bed, pushes it
/// to all stations, and broadcasts only the alerts whose state actually changed.
/// </summary>
public class VitalsGeneratorHostedService<THub> : BackgroundService where THub : Hub
{
    private readonly IVitalsGenerator _generator;
    private readonly IAlertStateService _alertState;
    private readonly IHubContext<THub> _hubContext;
    private readonly ILogger<VitalsGeneratorHostedService<THub>> _logger;

    public VitalsGeneratorHostedService(
        IVitalsGenerator generator,
        IAlertStateService alertState,
        IHubContext<THub> hubContext,
        ILogger<VitalsGeneratorHostedService<THub>> logger)
    {
        _generator = generator;
        _alertState = alertState;
        _hubContext = hubContext;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Vitals generator started — {BedCount} beds at {IntervalMs} ms.",
            _generator.GetPatients().Count, GeneratorSettings.UpdateIntervalMs);

        using var timer = new PeriodicTimer(GeneratorSettings.UpdateInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await PublishTickAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // One bad tick must not kill the simulation loop.
                    _logger.LogError(ex, "Error while generating or broadcasting a vitals tick.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }

        _logger.LogInformation("Vitals generator stopped.");
    }

    private async Task PublishTickAsync(CancellationToken cancellationToken)
    {
        foreach (var update in _generator.GenerateUpdatesForAllPatients())
        {
            await _hubContext.Clients.All.SendAsync(HubMethods.ReceiveVitals, update, cancellationToken);

            var changes = _alertState.Reconcile(update.PatientId, update.Vitals);
            if (!changes.HasChanges) continue;

            foreach (var alert in changes.Raised)
            {
                await _hubContext.Clients.All.SendAsync(HubMethods.AlertTriggered, alert, cancellationToken);
                _logger.LogWarning("ALERT {Severity} — {Patient}: {Message}",
                    alert.Severity, alert.PatientId, alert.Message);
            }

            foreach (var alert in changes.Cleared)
            {
                await _hubContext.Clients.All.SendAsync(HubMethods.AlertCleared, alert, cancellationToken);
                _logger.LogInformation("Alert cleared — {Patient}: {Vital}", alert.PatientId, alert.VitalSign);
            }
        }
    }
}
