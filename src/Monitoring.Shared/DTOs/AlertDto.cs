namespace Monitoring.Shared.DTOs;

/// <summary>
/// Alert raised when a vital sign breaches a threshold.
/// An alert is a *stateful* entity: it is raised once when the breach starts,
/// updated when its severity changes, and cleared when the vital returns to range.
/// </summary>
/// <param name="AlertId">
/// Stable identifier, unique per (patient, vital sign). It does NOT change while the
/// breach persists, so clients can deduplicate, acknowledge and clear by this key.
/// </param>
/// <param name="PatientId">Patient who triggered the alert.</param>
/// <param name="VitalSign">Which vital sign triggered it.</param>
/// <param name="Severity">Warning or Critical.</param>
/// <param name="Message">Human-readable alert message (e.g., "HR critically high: 180 bpm").</param>
/// <param name="Value">The measured value that triggered the alert.</param>
/// <param name="Timestamp">When the alert was first raised (preserved across value updates).</param>
/// <param name="IsAcknowledged">True once a clinician has acknowledged (silenced) the alert.</param>
public record AlertDto(
    string AlertId,
    string PatientId,
    VitalSignType VitalSign,
    AlertSeverity Severity,
    string Message,
    double Value,
    DateTimeOffset Timestamp,
    bool IsAcknowledged = false
)
{
    /// <summary>Builds the stable alert key for a patient/vital pair.</summary>
    public static string BuildId(string patientId, VitalSignType vitalSign) => $"{patientId}:{vitalSign}";
}
