using System.Threading.Channels;
using MedicalMonitorServer.Contracts.Api;
using MedicalMonitorServer.Contracts.Models;
using MedicalMonitorServer.Core.Monitoring;
using MedicalMonitorServer.DeviceDriver;

namespace MedicalMonitorServer.Services;

/// <summary>
/// Bridges decoded uMec HL7 messages from <see cref="DeviceService"/> into the monitoring
/// pipeline: admitted patients become roster entries, measured values become vitals frames.
/// Messages are queued so the device read loops are never blocked by SignalR broadcasting.
/// </summary>
public sealed class UmecFeedService : BackgroundService
{
    private readonly DeviceService _deviceService;
    private readonly IMonitorPipeline _pipeline;
    private readonly IPatientRegistry _patients;
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
        ILogger<UmecFeedService> logger)
    {
        _deviceService = deviceService;
        _pipeline = pipeline;
        _patients = patients;
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
        string? patientId = null;

        if (info is not null && !string.IsNullOrWhiteSpace(info.PatientId))
        {
            patientId = info.PatientId;
            await _pipeline.UpsertPatientAsync(ToPatientDto(info), ct);
        }

        var oru = message.Oru;
        if (oru is null) return;

        // Only the patient-info message carries a PID; vitals (503), alarms (54/56) and waveform
        // channels (160/161) do not. Fall back to the only patient on the roster; with several
        // patients the owner is ambiguous.
        string? Owner() => patientId ??= SingleKnownPatientId();

        if (oru.WaveChannels.Count > 0 && Owner() is { } waveOwner)
        {
            _patients.UpdateWaveformChannels(waveOwner, oru.WaveChannels.Select(ToChannelDto));
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
            if (Owner() is { } alarmOwner)
            {
                var active = oru.Alarms
                    .Where(a => a.AlarmId != 0)
                    .Select(a => ToAlertDto(alarmOwner, a, c))
                    .ToList();
                await _pipeline.IngestDeviceAlarmsAsync(alarmOwner, c, active, ct);
            }
            else
            {
                _logger.LogDebug("Alarm message {MessageId} dropped: patient unknown.", message.MessageId);
            }
        }

        if (oru.Vitals.Count == 0) return;

        var readings = ToReadings(oru.Vitals);
        if (readings == new VitalReadings()) return;

        if (Owner() is not { } vitalsOwner)
        {
            _logger.LogDebug("Vitals message {MessageId} dropped: patient unknown.", message.MessageId);
            return;
        }

        await _pipeline.IngestReadingsAsync(vitalsOwner, readings, null, DateTimeOffset.UtcNow, ct);
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

    private string? SingleKnownPatientId()
    {
        var all = _patients.GetAll();
        return all.Count == 1 ? all[0].PatientId : null;
    }

    private static PatientInfoDto ToPatientDto(PatientInfo info)
    {
        var name = !string.IsNullOrWhiteSpace(info.FullName)
            ? info.FullName
            : $"{info.LastName} {info.FirstName}".Trim();

        return new PatientInfoDto(
            info.PatientId,
            string.IsNullOrWhiteSpace(name) ? info.PatientId : name,
            int.TryParse(info.BedNumber, out var bed) ? bed : 0,
            null,
            info.PatientType switch
            {
                Contracts.Models.PatientType.Pediatric => "Ped",
                Contracts.Models.PatientType.Neonate => "Neo",
                _ => "Adu",
            });
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
