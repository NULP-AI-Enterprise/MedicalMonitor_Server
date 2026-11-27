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
/// <param name="Parameters">
/// The monitor's other values by <see cref="ParameterDto.Code"/> (see <see cref="MonitorParameters"/>);
/// null when there are none, never empty, so that readings with nothing in them compare equal.
/// </param>
/// <param name="WithdrawnParameters">
/// In an update only: codes of values the monitor has just reported as no longer measured,
/// which <see cref="Merge"/> takes off the snapshot. Never set on a stored snapshot.
/// </param>
/// <param name="NibpMeasuredAt">When the cuff reading was taken; null when the monitor did not say.</param>
/// <param name="CuffWithdrawn">
/// In an update only: the monitor lists its cuff pressure without a value - a measurement
/// failed, or the patient changed - so the reading it had no longer stands. That is not the
/// same as a report that does not mention the cuff, which leaves the last reading in place.
/// </param>
/// <param name="NibpMeasuring">
/// The monitor says a cuff measurement is running. A state of the moment, not a reading: it is
/// what the latest report says and never carried over from an earlier one, so a report that
/// does not say it ends it.
/// </param>
public sealed record VitalReadings(
    int? HeartRate = null,
    int? SpO2 = null,
    int? Systolic = null,
    int? Diastolic = null,
    int? MeanArterialPressure = null,
    int? RespiratoryRate = null,
    double? Temperature = null,
    int? PulseRate = null,
    IReadOnlyDictionary<string, ParameterDto>? Parameters = null,
    IReadOnlyCollection<string>? WithdrawnParameters = null,
    DateTimeOffset? NibpMeasuredAt = null,
    bool CuffWithdrawn = false,
    bool NibpMeasuring = false)
{
    /// <summary>
    /// Builds readings from a wire DTO. Both null and 0 mean "not measured" (0 is kept for
    /// older gateways) and become null, so an absent sensor never raises a critical alert.
    /// </summary>
    public static VitalReadings From(VitalsDto vitals) => new(
        Measured(vitals.HeartRate),
        Measured(vitals.SpO2),
        Measured(vitals.Nibp?.Systolic),
        Measured(vitals.Nibp?.Diastolic),
        Measured(vitals.Nibp?.MeanArterialPressure),
        Measured(vitals.RespiratoryRate),
        vitals.Temperature > 0 ? vitals.Temperature : null,
        Measured(vitals.PulseRate),
        vitals.Parameters is { Count: > 0 } parameters
            ? parameters.GroupBy(p => p.Code).ToDictionary(g => g.Key, g => g.Last())
            : null,
        NibpMeasuredAt: vitals.Nibp?.MeasuredAt,
        NibpMeasuring: vitals.Nibp?.Measuring ?? false);

    private static int? Measured(int? value) => value > 0 ? value : null;

    /// <summary>
    /// True when a value was measured. Readings that only withdraw values carry no measurement:
    /// they change what a station shows, but they are not data arriving.
    /// </summary>
    public bool HasValues =>
        HeartRate is not null || SpO2 is not null || Systolic is not null || Diastolic is not null
        || MeanArterialPressure is not null || RespiratoryRate is not null || Temperature is not null
        || PulseRate is not null || Parameters is not null;

    private bool HasCuffReading => Systolic is not null || Diastolic is not null || MeanArterialPressure is not null;

    /// <summary>
    /// Overlays the values present in <paramref name="update"/> on top of this snapshot, and
    /// takes off the parameters it withdraws.
    /// </summary>
    public VitalReadings Merge(VitalReadings update) => new(
        update.HeartRate ?? HeartRate,
        update.SpO2 ?? SpO2,
        update.Systolic ?? (update.CuffWithdrawn ? null : Systolic),
        update.Diastolic ?? (update.CuffWithdrawn ? null : Diastolic),
        update.MeanArterialPressure ?? (update.CuffWithdrawn ? null : MeanArterialPressure),
        update.RespiratoryRate ?? RespiratoryRate,
        update.Temperature ?? Temperature,
        update.PulseRate ?? PulseRate,
        MergeParameters(Parameters, update.Parameters, update.WithdrawnParameters),
        // The time belongs to the reading: a new reading without one must not keep the old time.
        NibpMeasuredAt: update.HasCuffReading ? update.NibpMeasuredAt : update.CuffWithdrawn ? null : NibpMeasuredAt,
        NibpMeasuring: update.NibpMeasuring);

    /// <summary>
    /// Takes <paramref name="snapshot"/> as everything the monitor measures right now: a running
    /// value it does not carry is dropped, not kept from before. Blood pressure is the exception -
    /// a cuff reading is taken now and then and stands until the next one, so a report that does
    /// not mention it leaves it alone. Only a new reading replaces it, and only the monitor
    /// withdrawing it (<see cref="CuffWithdrawn"/>) takes it off.
    /// </summary>
    public VitalReadings ReplaceRunning(VitalReadings snapshot) => new(
        snapshot.HeartRate,
        snapshot.SpO2,
        snapshot.Systolic ?? (snapshot.CuffWithdrawn ? null : Systolic),
        snapshot.Diastolic ?? (snapshot.CuffWithdrawn ? null : Diastolic),
        snapshot.MeanArterialPressure ?? (snapshot.CuffWithdrawn ? null : MeanArterialPressure),
        snapshot.RespiratoryRate,
        snapshot.Temperature,
        snapshot.PulseRate,
        snapshot.Parameters,
        NibpMeasuredAt: snapshot.HasCuffReading ? snapshot.NibpMeasuredAt : snapshot.CuffWithdrawn ? null : NibpMeasuredAt,
        NibpMeasuring: snapshot.NibpMeasuring);

    private static IReadOnlyDictionary<string, ParameterDto>? MergeParameters(
        IReadOnlyDictionary<string, ParameterDto>? current,
        IReadOnlyDictionary<string, ParameterDto>? update,
        IReadOnlyCollection<string>? withdrawn)
    {
        if (update is null && withdrawn is null) return current;

        var merged = current is null
            ? new Dictionary<string, ParameterDto>(StringComparer.Ordinal)
            : new Dictionary<string, ParameterDto>(current, StringComparer.Ordinal);

        foreach (var code in withdrawn ?? Array.Empty<string>()) merged.Remove(code);
        foreach (var (code, parameter) in update ?? new Dictionary<string, ParameterDto>()) merged[code] = parameter;

        return merged.Count > 0 ? merged : null;
    }

    /// <summary>Wire form: missing values stay null. Nibp is always an object, its fields may be null.</summary>
    public VitalsDto ToDto()
    {
        var map = MeanArterialPressure
            ?? (Systolic is int s && Diastolic is int d ? (int)Math.Round((s + 2.0 * d) / 3.0) : null);

        return new VitalsDto(
            HeartRate,
            SpO2,
            new BloodPressureDto(Systolic, Diastolic, map, HasCuffReading ? NibpMeasuredAt : null, NibpMeasuring),
            RespiratoryRate,
            Temperature,
            PulseRate,
            // In a fixed order, so a station's list does not reshuffle from one frame to the next.
            Parameters?.Values.OrderBy(p => p.Code, StringComparer.Ordinal).ToArray());
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
    /// <summary>
    /// Re-evaluates the server thresholds. Only alerts keyed by vital sign (no device code)
    /// can be cleared here; device alarms are managed by <see cref="ReconcileDevice"/>.
    /// </summary>
    AlertChangeSet Reconcile(string patientId, VitalReadings readings);

    /// <summary>
    /// Applies the complete list of device alarms of one category that are active on the
    /// patient's monitor right now: new ones are raised, missing ones cleared.
    /// </summary>
    AlertChangeSet ReconcileDevice(string patientId, AlertCategory category, IReadOnlyList<AlertDto> active);

    /// <summary>Registers an alert reported directly by a device gateway. Returns it when it is new or changed severity.</summary>
    AlertDto? Raise(AlertDto alert);

    AlertDto? Acknowledge(string alertId);

    /// <summary>Removes one alert. Returns it when it was active, null when there was nothing to remove.</summary>
    AlertDto? Clear(string alertId);

    IReadOnlyList<AlertDto> GetActiveAlerts();

    IReadOnlyList<AlertDto> ClearForPatient(string patientId);
}

public sealed class AlertStateService : IAlertStateService
{
    private readonly ConcurrentDictionary<string, AlertDto> _active = new();
    private readonly object _lock = new();

    public AlertChangeSet Reconcile(string patientId, VitalReadings readings) =>
        Apply(patientId, AlertEvaluator.Evaluate(patientId, readings), a => a.Code is null);

    public AlertChangeSet ReconcileDevice(string patientId, AlertCategory category, IReadOnlyList<AlertDto> active) =>
        Apply(
            patientId,
            active.Where(a => a.PatientId == patientId).ToList(),
            a => a.Source == AlertSource.Device && a.Code is not null && a.Category == category);

    /// <summary>
    /// Makes <paramref name="candidates"/> the active set among the patient's alerts selected by
    /// <paramref name="owned"/>: raises new or re-prioritised ones, clears owned ones no longer present.
    /// </summary>
    private AlertChangeSet Apply(string patientId, IReadOnlyList<AlertDto> candidates, Func<AlertDto, bool> owned)
    {
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
                        _active[candidate.AlertId] = existing with
                        {
                            Value = candidate.Value,
                            Message = candidate.Message,
                            Level = candidate.Level,
                        };
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
                         .Where(a => a.PatientId == patientId && owned(a) && !candidateIds.Contains(a.AlertId))
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
                _active[alert.AlertId] = existing with { Value = alert.Value, Message = alert.Message, Level = alert.Level };
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

    public AlertDto? Clear(string alertId)
    {
        if (string.IsNullOrWhiteSpace(alertId)) return null;

        lock (_lock)
        {
            return _active.TryRemove(alertId, out var removed) ? removed : null;
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
