using System.Threading.Channels;
using MedicalMonitorServer.Contracts.Api;
using MedicalMonitorServer.Contracts.Models;
using MedicalMonitorServer.Core.Monitoring;
using MedicalMonitorServer.DeviceDriver;

namespace MedicalMonitorServer.Services;

/// <summary>
/// Bridges decoded uMec HL7 messages from <see cref="DeviceService"/> into the monitoring
/// pipeline: measured values become vitals frames of the patient bound to the sending monitor
/// (see <see cref="MonitorRouter"/>).
/// Messages are queued so the device read loops are never blocked by SignalR broadcasting.
/// </summary>
public sealed class UmecFeedService : BackgroundService
{
    private readonly DeviceService _deviceService;
    private readonly IMonitorPipeline _pipeline;
    private readonly MonitorRouter _router;
    private readonly ILogger<UmecFeedService> _logger;

    private readonly Channel<UmecMessage> _queue = Channel.CreateBounded<UmecMessage>(
        new BoundedChannelOptions(1024)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

    public UmecFeedService(
        DeviceService deviceService,
        IMonitorPipeline pipeline,
        MonitorRouter router,
        ILogger<UmecFeedService> logger)
    {
        _deviceService = deviceService;
        _pipeline = pipeline;
        _router = router;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _deviceService.OnMessageReceived += Enqueue;
        try
        {
            await foreach (var message in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await HandleAsync(message, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Failed to process uMec message {MessageId}.", message.MessageId);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _deviceService.OnMessageReceived -= Enqueue;
        }
    }

    private void Enqueue(UmecMessage message) => _queue.Writer.TryWrite(message);

    private async Task HandleAsync(UmecMessage message, CancellationToken ct)
    {
        var info = message.Oru?.Patient ?? message.Adt?.Patient;

        var vitals = message.Oru?.Vitals;
        var readings = vitals is { Count: > 0 } ? ToReadings(vitals) : new VitalReadings();
        var hasReadings = readings != new VitalReadings();

        // Most messages carry no PID at all (the monitor sends it only in its patient-info
        // message), so the owner comes from the monitor the message was read from.
        var monitorIp = !string.IsNullOrWhiteSpace(message.SourceIp)
            ? message.SourceIp
            : info?.MonitorIp ?? string.Empty;

        var patientId = await _router.ResolveAsync(monitorIp, info, hasReadings, ct);
        if (patientId is null || !hasReadings) return;

        await _pipeline.IngestReadingsAsync(patientId, readings, null, DateTimeOffset.UtcNow, ct);
    }

    /// <summary>Maps Mindray parameter ids to the vitals the UI shows.</summary>
    private static VitalReadings ToReadings(IEnumerable<VitalSign> vitals)
    {
        var readings = new VitalReadings();

        foreach (var vital in vitals)
        {
            if (vital.Value is not double value) continue;

            var whole = (int)Math.Round(value);
            readings = vital.ParameterId switch
            {
                101 => readings with { HeartRate = whole },
                151 => readings with { RespiratoryRate = whole },
                160 => readings with { SpO2 = whole },
                161 or 600 => readings with { PulseRate = whole },
                170 => readings with { Systolic = whole },
                171 => readings with { Diastolic = whole },
                172 => readings with { MeanArterialPressure = whole },
                200 => readings with { Temperature = Math.Round(value, 1) },
                _ => readings,
            };
        }

        return readings;
    }
}
