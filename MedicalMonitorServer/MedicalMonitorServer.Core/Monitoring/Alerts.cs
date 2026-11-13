using System.Collections.Concurrent;
using MedicalMonitorServer.Contracts.Api;

namespace MedicalMonitorServer.Core.Monitoring;

/// <summary>Adult clinical thresholds used to raise alerts (see docs/API_CONTRACT.md §7).</summary>
public static class VitalThresholds
{
    public const int HeartRateCriticalLow = 40;
    public const int HeartRateWarningLow = 50;
    public const int HeartRateWarningHigh = 110;
    public const int HeartRateCriticalHigh = 150;

    public const int SpO2CriticalLow = 88;
    public const int SpO2WarningLow = 92;

    public const int SystolicCriticalLow = 70;
    public const int SystolicWarningLow = 85;
    public const int SystolicWarningHigh = 140;
    public const int SystolicCriticalHigh = 180;

    public const int DiastolicCriticalLow = 45;
    public const int DiastolicWarningLow = 55;
    public const int DiastolicWarningHigh = 95;
    public const int DiastolicCriticalHigh = 110;

    public const int RespRateCriticalLow = 8;
    public const int RespRateWarningLow = 10;
    public const int RespRateWarningHigh = 24;
    public const int RespRateCriticalHigh = 30;

    public const double TempCriticalLow = 35.0;
    public const double TempWarningLow = 36.0;
    public const double TempWarningHigh = 37.8;
    public const double TempCriticalHigh = 39.0;
}

/// <summary>
/// Latest known readings of a patient. A null value means "not measured (yet)" and is
/// never evaluated, so a missing sensor cannot raise a false critical alert.
/// </summary>
public sealed record VitalReadings(
    int? HeartRate = null,
    int? SpO2 = null,
    int? Systolic = null,
    int? Diastolic = null,
    int? MeanArterialPressure = null,
    int? RespiratoryRate = null,
    double? Temperature = null,
    int? PulseRate = null)
{
    public static VitalReadings From(VitalsDto vitals) => new(
        vitals.HeartRate,
        vitals.SpO2,
        vitals.Nibp?.Systolic,
        vitals.Nibp?.Diastolic,
        vitals.Nibp?.MeanArterialPressure,
        vitals.RespiratoryRate,
        vitals.Temperature,
        vitals.PulseRate);

    /// <summary>Overlays the values present in <paramref name="update"/> on top of this snapshot.</summary>
    public VitalReadings Merge(VitalReadings update) => new(
        update.HeartRate ?? HeartRate,
        update.SpO2 ?? SpO2,
        update.Systolic ?? Systolic,
        update.Diastolic ?? Diastolic,
        update.MeanArterialPressure ?? MeanArterialPressure,
        update.RespiratoryRate ?? RespiratoryRate,
        update.Temperature ?? Temperature,
        update.PulseRate ?? PulseRate);

    public VitalsDto ToDto()
    {
        var map = MeanArterialPressure
            ?? (Systolic is int s && Diastolic is int d ? (int)Math.Round((s + 2.0 * d) / 3.0) : 0);

        return new VitalsDto(
            HeartRate ?? 0,
            SpO2 ?? 0,
            new BloodPressureDto(Systolic ?? 0, Diastolic ?? 0, map),
            RespiratoryRate ?? 0,
            Temperature ?? 0,
            PulseRate ?? 0);
    }
}

/// <summary>Stateless threshold evaluation: which alerts should be active for these readings.</summary>
public static class AlertEvaluator
{
    public static List<AlertDto> Evaluate(string patientId, VitalReadings v)
    {
        var alerts = new List<AlertDto>();

        Check(alerts, patientId, VitalSignType.HeartRate, v.HeartRate,
            VitalThresholds.HeartRateCriticalLow, VitalThresholds.HeartRateWarningLow,
            VitalThresholds.HeartRateWarningHigh, VitalThresholds.HeartRateCriticalHigh);

        // SpO2 has no upper bound: 100% is never an alarm.
        Check(alerts, patientId, VitalSignType.SpO2, v.SpO2,
            VitalThresholds.SpO2CriticalLow, VitalThresholds.SpO2WarningLow,
            double.PositiveInfinity, double.PositiveInfinity);

        Check(alerts, patientId, VitalSignType.Systolic, v.Systolic,
            VitalThresholds.SystolicCriticalLow, VitalThresholds.SystolicWarningLow,
            VitalThresholds.SystolicWarningHigh, VitalThresholds.SystolicCriticalHigh);

        Check(alerts, patientId, VitalSignType.Diastolic, v.Diastolic,
            VitalThresholds.DiastolicCriticalLow, VitalThresholds.DiastolicWarningLow,
            VitalThresholds.DiastolicWarningHigh, VitalThresholds.DiastolicCriticalHigh);

        Check(alerts, patientId, VitalSignType.RespiratoryRate, v.RespiratoryRate,
            VitalThresholds.RespRateCriticalLow, VitalThresholds.RespRateWarningLow,
            VitalThresholds.RespRateWarningHigh, VitalThresholds.RespRateCriticalHigh);

        Check(alerts, patientId, VitalSignType.Temperature, v.Temperature,
            VitalThresholds.TempCriticalLow, VitalThresholds.TempWarningLow,
            VitalThresholds.TempWarningHigh, VitalThresholds.TempCriticalHigh);

        return alerts;
    }

    private static void Check(List<AlertDto> alerts, string patientId, VitalSignType type, double? value,
        double criticalLow, double warningLow, double warningHigh, double criticalHigh)
    {
        if (value is not double x) return;

        if (x < criticalLow) alerts.Add(Create(patientId, type, x, AlertSeverity.Critical, "critically low"));
        else if (x > criticalHigh) alerts.Add(Create(patientId, type, x, AlertSeverity.Critical, "critically high"));
        else if (x < warningLow) alerts.Add(Create(patientId, type, x, AlertSeverity.Warning, "low"));
        else if (x > warningHigh) alerts.Add(Create(patientId, type, x, AlertSeverity.Warning, "high"));
    }

    private static AlertDto Create(string patientId, VitalSignType type, double value, AlertSeverity severity, string direction)
    {
        var formatted = type == VitalSignType.Temperature
            ? value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)
            : Math.Round(value).ToString("F0", System.Globalization.CultureInfo.InvariantCulture);

        return new AlertDto(
            AlertDto.BuildId(patientId, type),
            patientId,
            type,
            severity,
            $"{VitalSignLabels.Short(type)} {direction}: {formatted} {VitalSignLabels.Unit(type)}".TrimEnd(),
            value,
            DateTimeOffset.UtcNow);
    }
}

/// <summary>Alerts that changed during one reconcile pass.</summary>
public readonly record struct AlertChangeSet(IReadOnlyList<AlertDto> Raised, IReadOnlyList<AlertDto> Cleared)
{
    public static readonly AlertChangeSet Empty = new(Array.Empty<AlertDto>(), Array.Empty<AlertDto>());

    public bool HasChanges => Raised.Count > 0 || Cleared.Count > 0;
}

/// <summary>Tracks active alerts so only transitions (raise / escalate / clear) are broadcast.</summary>
public interface IAlertStateService
{
    AlertChangeSet Reconcile(string patientId, VitalReadings readings);

    /// <summary>Registers an alert reported directly by a device gateway. Returns it when it is new or changed severity.</summary>
    AlertDto? Raise(AlertDto alert);

    AlertDto? Acknowledge(string alertId);

    IReadOnlyList<AlertDto> GetActiveAlerts();

    IReadOnlyList<AlertDto> ClearForPatient(string patientId);
}

public sealed class AlertStateService : IAlertStateService
{
    private readonly ConcurrentDictionary<string, AlertDto> _active = new();
    private readonly object _lock = new();

    public AlertChangeSet Reconcile(string patientId, VitalReadings readings)
    {
        var candidates = AlertEvaluator.Evaluate(patientId, readings);

        lock (_lock)
        {
            List<AlertDto>? raised = null;
            List<AlertDto>? cleared = null;

            foreach (var candidate in candidates)
            {
                if (_active.TryGetValue(candidate.AlertId, out var existing))
                {
                    if (existing.Severity == candidate.Severity)
                    {
                        // Same breach: refresh the value silently, keep raise time and acknowledgement.
                        _active[candidate.AlertId] = existing with { Value = candidate.Value, Message = candidate.Message };
                        continue;
                    }

                    // Severity changed: re-alarm, acknowledgement does not carry over.
                    var changed = candidate with { Timestamp = existing.Timestamp };
                    _active[candidate.AlertId] = changed;
                    (raised ??= new()).Add(changed);
                    continue;
                }

                _active[candidate.AlertId] = candidate;
                (raised ??= new()).Add(candidate);
            }

            var candidateIds = candidates.Select(a => a.AlertId).ToHashSet();
            foreach (var stale in _active.Values
                         .Where(a => a.PatientId == patientId && !candidateIds.Contains(a.AlertId))
                         .ToList())
            {
                if (_active.TryRemove(stale.AlertId, out var removed)) (cleared ??= new()).Add(removed);
            }

            if (raised is null && cleared is null) return AlertChangeSet.Empty;

            return new AlertChangeSet(
                (IReadOnlyList<AlertDto>?)raised ?? Array.Empty<AlertDto>(),
                (IReadOnlyList<AlertDto>?)cleared ?? Array.Empty<AlertDto>());
        }
    }

    public AlertDto? Raise(AlertDto alert)
    {
        lock (_lock)
        {
            if (_active.TryGetValue(alert.AlertId, out var existing) && existing.Severity == alert.Severity)
            {
                _active[alert.AlertId] = existing with { Value = alert.Value, Message = alert.Message };
                return null;
            }

            _active[alert.AlertId] = alert with { IsAcknowledged = false };
            return _active[alert.AlertId];
        }
    }

    public AlertDto? Acknowledge(string alertId)
    {
        if (string.IsNullOrWhiteSpace(alertId)) return null;

        lock (_lock)
        {
            if (!_active.TryGetValue(alertId, out var existing)) return null;
            if (existing.IsAcknowledged) return existing;

            var acknowledged = existing with { IsAcknowledged = true };
            _active[alertId] = acknowledged;
            return acknowledged;
        }
    }

    public IReadOnlyList<AlertDto> GetActiveAlerts() => _active.Values.OrderBy(a => a.Timestamp).ToList();

    public IReadOnlyList<AlertDto> ClearForPatient(string patientId)
    {
        lock (_lock)
        {
            var removed = new List<AlertDto>();
            foreach (var alert in _active.Values.Where(a => a.PatientId == patientId).ToList())
            {
                if (_active.TryRemove(alert.AlertId, out var gone)) removed.Add(gone);
            }

            return removed;
        }
    }
}
