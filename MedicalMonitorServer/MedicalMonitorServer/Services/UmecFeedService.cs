using System.Threading.Channels;
using MedicalMonitorServer.Contracts.Api;
using MedicalMonitorServer.Contracts.Models;
using MedicalMonitorServer.Core.Monitoring;
using MedicalMonitorServer.DeviceDriver;

namespace MedicalMonitorServer.Services;

/// <summary>
/// Bridges decoded uMec HL7 messages from <see cref="DeviceService"/> into the monitoring
/// pipeline: measured values, device alarms, waveform channels and waveform samples go to the
/// patient bound to the sending monitor (see <see cref="MonitorRouter"/>).
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

    // Channel descriptors by monitor, then by channel id. Kept per monitor and not read back
    // from the patient registry: a block of samples has to be scaled by the descriptor of the
    // monitor it came from, whoever that monitor is feeding and whether or not anybody was
    // bound to it when the descriptor arrived. Touched by the queue's single reader only.
    private readonly Dictionary<string, Dictionary<int, WaveChannel>> _waveChannels = new(StringComparer.Ordinal);

    // What was last logged about each monitor channel's trace; blocks arrive every second.
    private readonly Dictionary<(string MonitorIp, int ChannelId), string> _loggedTraces = new();

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

        // Not for waveform blocks: three a second per monitor, and nothing in them to read.
        if (_logger.IsEnabled(LogLevel.Debug) && message.Kind != UmecMessageKind.Waveforms)
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

        // Before anything can return: the monitor describes its channels once, right after
        // connecting, which may be before it has a patient.
        if (oru is { WaveChannels.Count: > 0 })
        {
            RememberChannels(monitorIp, oru.WaveChannels);
        }

        var patientId = await _router.ResolveAsync(monitorIp, info, hasReadings, ct);

        // Before the early return below, or the messages that were dropped for lack
        // of a patient would be exactly the ones the summary never saw.
        Summarise(monitorIp, patientId, oru);

        if (oru is null || patientId is null) return;

        if (oru.WaveChannels.Count > 0)
        {
            _patients.UpdateWaveformChannels(patientId, oru.WaveChannels.Select(UmecWaveforms.ToChannelDto));
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

        var waveforms = ToWaveforms(monitorIp, oru.WaveSamples);
        if (oru.ScaledWaves.Count > 0)
        {
            // A Nihon Kohden over LS-NET: no descriptors to scale by, the driver did it.
            waveforms = (waveforms ?? Array.Empty<WaveformSampleDto>())
                .Concat(oru.ScaledWaves.Select(w => new WaveformSampleDto(w.Type, w.Samples, w.SampleRate)))
                .ToArray();
        }
        if (!hasReadings)
        {
            // The monitor sends samples in a message of their own, without values.
            if (waveforms is not null)
            {
                await _pipeline.IngestWaveformsAsync(patientId, waveforms, DateTimeOffset.UtcNow, ct);
            }

            return;
        }

        await _pipeline.IngestReadingsAsync(patientId, readings, waveforms, DateTimeOffset.UtcNow, ct);
    }

    private void RememberChannels(string monitorIp, IEnumerable<WaveChannel> channels)
    {
        if (!_waveChannels.TryGetValue(monitorIp, out var known))
        {
            _waveChannels[monitorIp] = known = new Dictionary<int, WaveChannel>();
        }

        foreach (var channel in channels)
        {
            known[channel.ChannelId] = channel;
        }
    }

    /// <summary>
    /// Scales the blocks of samples of one message by the descriptors their monitor sent.
    /// Null when the message has none a station can draw.
    /// </summary>
    private WaveformSampleDto[]? ToWaveforms(string monitorIp, IReadOnlyList<WaveSamples> blocks)
    {
        if (blocks.Count == 0) return null;

        _waveChannels.TryGetValue(monitorIp, out var known);

        List<WaveformSampleDto>? waveforms = null;
        foreach (var block in blocks)
        {
            WaveChannel? channel = null;
            known?.TryGetValue(block.ChannelId, out channel);

            var waveform = channel is null ? null : UmecWaveforms.ToSampleDto(channel, block);
            if (waveform is not null) (waveforms ??= new()).Add(waveform);

            LogTrace(monitorIp, block.ChannelId, channel, waveform);
        }

        return waveforms?.ToArray();
    }

    /// <summary>One line when a monitor channel starts to be drawn, stops, or changes lead or rate.</summary>
    private void LogTrace(string monitorIp, int channelId, WaveChannel? channel, WaveformSampleDto? waveform)
    {
        var state = waveform is not null ? $"sent to stations as {waveform.Type} at {waveform.SampleRate} Hz"
            : channel is null ? "not drawn: the monitor has not described this channel"
            : UmecWaveforms.TypeOf(channel) is null ? "not drawn: stations have no trace for it"
            : "not drawn: its descriptor gives no usable scale";

        var key = (monitorIp, channelId);
        if (_loggedTraces.TryGetValue(key, out var logged) && logged == state) return;

        _loggedTraces[key] = state;
        _logger.LogInformation(
            "Waveform channel {ChannelId} ({Label}) of monitor {MonitorIp} is {State}.",
            channelId, channel?.Label ?? "?", monitorIp, state);
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
