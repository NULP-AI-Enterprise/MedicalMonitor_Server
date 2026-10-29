namespace Monitoring.Shared;

/// <summary>
/// Canonical SignalR method names for the vitals hub.
/// Both the server and the .NET client reference these constants so the two can
/// never drift apart; the JavaScript client mirrors them in <c>wwwroot/app.js</c>.
/// </summary>
public static class HubMethods
{
    /// <summary>Hub route the server maps and every client connects to.</summary>
    public const string Route = "/hubs/vitals";

    // ── Server → Client ───────────────────────────────────────
    /// <summary>Payload: <see cref="DTOs.MonitorUpdateDto"/>. Vitals + waveform batch for one patient.</summary>
    public const string ReceiveVitals = "ReceiveVitals";

    /// <summary>Payload: <see cref="DTOs.PatientInfoDto"/>. A monitor came online.</summary>
    public const string PatientAdded = "PatientAdded";

    /// <summary>Payload: <c>string</c> patientId. A monitor went offline.</summary>
    public const string PatientRemoved = "PatientRemoved";

    /// <summary>Payload: <see cref="DTOs.AlertDto"/>. Alert raised, or its severity changed.</summary>
    public const string AlertTriggered = "AlertTriggered";

    /// <summary>Payload: <see cref="DTOs.AlertDto"/>. The vital returned to range; drop it by AlertId.</summary>
    public const string AlertCleared = "AlertCleared";

    /// <summary>Payload: <see cref="DTOs.AlertDto"/>. A clinician silenced the alert.</summary>
    public const string AlertAcknowledged = "AlertAcknowledged";

    // ── Client → Server ───────────────────────────────────────
    /// <summary>Arg: <c>string</c> alertId. Silences an active alert for every connected station.</summary>
    public const string AcknowledgeAlert = "AcknowledgeAlert";

    /// <summary>Returns <see cref="DTOs.PatientInfoDto"/>[] — the current monitor roster.</summary>
    public const string GetPatients = "GetPatients";

    /// <summary>Returns <see cref="DTOs.AlertDto"/>[] — alerts already active when the client joined.</summary>
    public const string GetActiveAlerts = "GetActiveAlerts";
}
