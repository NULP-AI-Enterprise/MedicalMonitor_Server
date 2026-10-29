namespace Monitoring.BL.Services;

using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using Monitoring.Shared.DTOs;

/// <inheritdoc cref="IAlertStateService"/>
public sealed class AlertStateService : IAlertStateService
{
    // Keyed by AlertDto.AlertId, which is stable per (patient, vital sign).
    private readonly ConcurrentDictionary<string, AlertDto> _active = new();
    private readonly object _reconcileLock = new();

    public AlertChangeSet Reconcile(string patientId, VitalsDto vitals)
    {
        var candidates = AlertEvaluator.Evaluate(patientId, vitals);

        // Reconciling is read-modify-write across several keys, so it must be atomic
        // relative to other reconciles for the same patient.
        lock (_reconcileLock)
        {
            List<AlertDto>? raised = null;
            List<AlertDto>? cleared = null;

            foreach (var candidate in candidates)
            {
                if (_active.TryGetValue(candidate.AlertId, out var existing))
                {
                    if (existing.Severity == candidate.Severity)
                    {
                        // Same breach, new reading: refresh the value but keep the original
                        // raise time and the acknowledgement, and stay silent.
                        _active[candidate.AlertId] = existing with
                        {
                            Value = candidate.Value,
                            Message = candidate.Message
                        };
                        continue;
                    }

                    // Severity changed (e.g. Warning → Critical). Re-alarm: a previous
                    // acknowledgement does not carry over to a more serious condition.
                    var escalated = candidate with { Timestamp = existing.Timestamp };
                    _active[candidate.AlertId] = escalated;
                    (raised ??= new()).Add(escalated);
                    continue;
                }

                _active[candidate.AlertId] = candidate;
                (raised ??= new()).Add(candidate);
            }

            // Anything still active for this patient that no longer breaches has recovered.
            var candidateIds = candidates.Select(a => a.AlertId).ToHashSet();
            var stale = _active.Values
                .Where(a => a.PatientId == patientId && !candidateIds.Contains(a.AlertId))
                .ToList();

            foreach (var alert in stale)
            {
                if (_active.TryRemove(alert.AlertId, out var removed))
                    (cleared ??= new()).Add(removed);
            }

            if (raised is null && cleared is null) return AlertChangeSet.Empty;

            return new AlertChangeSet(
                (IReadOnlyList<AlertDto>?)raised ?? System.Array.Empty<AlertDto>(),
                (IReadOnlyList<AlertDto>?)cleared ?? System.Array.Empty<AlertDto>());
        }
    }

    public AlertDto? Acknowledge(string alertId)
    {
        if (string.IsNullOrWhiteSpace(alertId)) return null;

        while (_active.TryGetValue(alertId, out var existing))
        {
            if (existing.IsAcknowledged) return existing;

            var acknowledged = existing with { IsAcknowledged = true };
            if (_active.TryUpdate(alertId, acknowledged, existing)) return acknowledged;
            // Lost a race against a concurrent reconcile — re-read and retry.
        }

        return null;
    }

    public IReadOnlyList<AlertDto> GetActiveAlerts() => _active.Values.ToList();

    public IReadOnlyList<AlertDto> ClearForPatient(string patientId)
    {
        lock (_reconcileLock)
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
