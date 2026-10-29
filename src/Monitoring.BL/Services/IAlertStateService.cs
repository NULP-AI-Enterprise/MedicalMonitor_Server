namespace Monitoring.BL.Services;

using System.Collections.Generic;
using Monitoring.Shared.DTOs;

/// <summary>The alerts that changed state during a single reconcile pass.</summary>
/// <param name="Raised">Newly raised, or escalated/de-escalated to a different severity.</param>
/// <param name="Cleared">No longer breaching — clients should drop these by AlertId.</param>
public readonly record struct AlertChangeSet(
    IReadOnlyList<AlertDto> Raised,
    IReadOnlyList<AlertDto> Cleared
)
{
    public static readonly AlertChangeSet Empty = new(System.Array.Empty<AlertDto>(), System.Array.Empty<AlertDto>());

    public bool HasChanges => Raised.Count > 0 || Cleared.Count > 0;
}

/// <summary>
/// Tracks which alerts are currently active so the hub broadcasts on *transitions*
/// only, instead of re-sending every breach on every tick.
/// </summary>
public interface IAlertStateService
{
    /// <summary>
    /// Compares the patient's current vitals against the alerts already active for them
    /// and returns only what changed. Silently refreshes the value of unchanged alerts.
    /// </summary>
    AlertChangeSet Reconcile(string patientId, VitalsDto vitals);

    /// <summary>
    /// Marks an alert as acknowledged (silenced). Returns the updated alert, or
    /// <c>null</c> when the alert has already cleared or was never active.
    /// </summary>
    AlertDto? Acknowledge(string alertId);

    /// <summary>Every alert currently active, for replay to a client that just connected.</summary>
    IReadOnlyList<AlertDto> GetActiveAlerts();

    /// <summary>Drops all alerts for a patient whose monitor went offline.</summary>
    IReadOnlyList<AlertDto> ClearForPatient(string patientId);
}
