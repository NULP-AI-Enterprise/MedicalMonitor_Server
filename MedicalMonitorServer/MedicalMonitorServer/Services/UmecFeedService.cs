using System.Threading.Channels;
using MedicalMonitorServer.Contracts.Api;
using MedicalMonitorServer.Contracts.Models;
using MedicalMonitorServer.Core.Monitoring;
using MedicalMonitorServer.DeviceDriver;

namespace MedicalMonitorServer.Services;

/// <summary>
/// Bridges decoded uMec HL7 messages from <see cref="DeviceService"/> into the monitoring
/// pipeline: measured values, device alarms and waveform channels go to the patient bound to
/// the sending monitor (see <see cref="MonitorRouter"/>).
/// Messages are queued so the device read loops are never blocked by SignalR broadcasting.
/// </summary>
public sealed class UmecFeedService : BackgroundService
{
    private readonly DeviceService _deviceService;
    private readonly IMonitorPipeline _pipeline;
    private readonly IPatientRegistry _patients;
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
        IPatientRegistry patients,
        MonitorRouter router,
        ILogger<UmecFeedService> logger)
    {
        _deviceService = deviceService;
        _pipeline = pipeline;
        _patients = patients;
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

    /// <summary>
    /// How often each monitor's traffic is summed up in one Information line.
    /// The per-message detail stays at Debug: at about ten messages a second it
    /// would push the alert history out of the log file.
    /// </summary>
    public TimeSpan SummaryInterval { get; set; } = TimeSpan.FromMinutes(1);

    private readonly FeedSummary _summary = new();

    private void Summarise(string monitorIp, string? patientId, UmecVitalsMessage? oru)
    {
        var line = _summary.Add(
            monitorIp.Length == 0 ? "(unknown)" : monitorIp,
            patientId,
            oru?.Vitals.Count(v => v.Value is not null) ?? 0,
            oru?.Alarms.Count(a => a.AlarmId != 0) ?? 0,
            DateTimeOffset.UtcNow,
            SummaryInterval);

        if (line is not { } summary) return;

        // The reason for a drop is logged where it is decided, see MonitorRouter.LogDrop.
        _logger.LogInformation(
            "uMec feed from {MonitorIp} for patient {PatientId}: {Messages} messages in {Seconds:0} s, " +
            "{Vitals} measured values, {Alarms} active alarm entries, " +
            "{Dropped} messages with data dropped for lack of a patient.",
            summary.MonitorIp,
            summary.PatientId ?? "(none)",
            summary.Messages,
            summary.Elapsed.TotalSeconds,
            summary.Vitals,
            summary.Alarms,
            summary.Dropped);
    }

    private async Task HandleAsync(UmecMessage message, CancellationToken ct)
    {
        var info = message.Oru?.Patient ?? message.Adt?.Patient;
        var oru = message.Oru;

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            // Raw values as the monitor sent them, before sentinels (-100 etc.) become null.
            _logger.LogDebug("uMec msg {MessageId} ({Kind}) from {Ip}: vitals [{Vitals}] alarms [{Alarms}]",
                message.MessageId, message.Kind, message.SourceIp,
                string.Join(", ", oru?.Vitals.Select(v => $"{v.ParameterId} {v.Name}={v.RawValue}") ?? []),
                string.Join(", ", oru?.Alarms.Select(a => $"{a.AlarmId}:{a.Text}") ?? []));

            if (message.Kind == UmecMessageKind.Unknown)
            {
                _logger.LogDebug("uMec raw {MessageId}: {Raw}", message.MessageId, message.Raw.Replace("\r", " ¦ "));
            }
        }

        var readings = oru is { Vitals.Count: > 0 } ? ToReadings(oru.Vitals) : new VitalReadings();
        var hasReadings = readings != new VitalReadings();

        // Most messages carry no PID at all (the monitor sends it only in its patient-info
        // message), so the owner comes from the monitor the message was read from.
        var monitorIp = !string.IsNullOrWhiteSpace(message.SourceIp)
            ? message.SourceIp
            : info?.MonitorIp ?? string.Empty;

        var patientId = await _router.ResolveAsync(monitorIp, info, hasReadings, ct);

        // Before the early return below, or the messages that were dropped for lack
        // of a patient would be exactly the ones the summary never saw.
        Summarise(monitorIp, patientId, oru);

        if (oru is null || patientId is null) return;

        if (oru.WaveChannels.Count > 0)
        {
            _patients.UpdateWaveformChannels(patientId, oru.WaveChannels.Select(ToChannelDto));
        }

        // Msg 54 / 56 list every alarm of that category active right now; an empty list means none.
        var category = message.Kind switch
        {
            UmecMessageKind.PhysiologicalAlarms => AlertCategory.Physiological,
            UmecMessageKind.TechnicalAlarms => AlertCategory.Technical,
            _ => (AlertCategory?)null,
        };
        if (category is { } c)
        {
            var active = oru.Alarms
                .Where(a => a.AlarmId != 0)
                .Select(a => ToAlertDto(patientId, a, c))
                .ToList();
            await _pipeline.IngestDeviceAlarmsAsync(patientId, c, active, ct);
        }

        if (!hasReadings) return;

        await _pipeline.IngestReadingsAsync(patientId, readings, null, DateTimeOffset.UtcNow, ct);
    }

    private static AlertDto ToAlertDto(string patientId, ActiveAlarm alarm, AlertCategory category)
    {
        DeviceAlarmLevel? level = alarm.Level switch
        {
            AlarmLevel.High => DeviceAlarmLevel.High,
            AlarmLevel.Medium => DeviceAlarmLevel.Medium,
            AlarmLevel.Low => DeviceAlarmLevel.Low,
            _ => null,
        };

        return new AlertDto(
            AlertDto.BuildDeviceId(patientId, alarm.AlarmId),
            patientId,
            VitalSign: null,
            AlertDto.SeverityOf(level),
            string.IsNullOrWhiteSpace(alarm.Text) ? $"Device alarm {alarm.AlarmId}" : alarm.Text,
            Value: null,
            DateTimeOffset.UtcNow,
            Source: AlertSource.Device,
            Code: alarm.AlarmId,
            Level: level,
            Category: category);
    }

    private static WaveformChannelDto ToChannelDto(WaveChannel c) => new(
        c.ChannelId,
        c.ChannelId switch
        {
            1151 => WaveformType.Respiration,
            1152 => WaveformType.Pleth,
            >= 1100 and < 1150 => c.Label switch
            {
                "II" => WaveformType.ECG_II,
                "I" => WaveformType.ECG_I,
                _ => null,
            },
            _ => null,
        },
        c.Label,
        c.Scale,
        c.ScaleUnit,
        c.Baseline,
        c.Levels,
        c.MinSample,
        c.MaxSample);

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
