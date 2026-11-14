using MedicalMonitorServer.Contracts.Api;
using MedicalMonitorServer.Core.Monitoring;
using MedicalMonitorServer.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace MedicalMonitorServer.Services;

/// <summary>Counts nurse stations currently connected to the hub.</summary>
public sealed class ConnectionTracker
{
    private int _count;

    public int Count => Volatile.Read(ref _count);

    public void Increment() => Interlocked.Increment(ref _count);

    public void Decrement() => Interlocked.Decrement(ref _count);
}

/// <summary>
/// Single entry point for everything that changes monitoring state, whether it comes
/// from the uMec feed or the REST ingestion endpoints. Updates the registry and alert
/// state, then broadcasts the matching SignalR messages.
/// </summary>
public interface IMonitorPipeline
{
    /// <summary>Adds or updates a patient. Broadcasts PatientAdded when something changed.</summary>
    Task UpsertPatientAsync(PatientInfoDto patient, CancellationToken ct = default);

    Task<bool> RemovePatientAsync(string patientId, CancellationToken ct = default);

    /// <summary>
    /// Drops a patient's vitals snapshot and active alerts (broadcasts AlertCleared). Used when
    /// the patient changes monitors, so values from the old device do not linger.
    /// </summary>
    Task ResetReadingsAsync(string patientId, CancellationToken ct = default);

    /// <summary>Applies a telemetry frame: registers the patient if new, merges vitals, reconciles alerts, broadcasts ReceiveVitals.</summary>
    Task IngestTelemetryAsync(MonitorUpdateDto update, CancellationToken ct = default);

    /// <summary>Applies partial readings (only the values the device reported) for a known patient.</summary>
    Task IngestReadingsAsync(string patientId, VitalReadings readings, WaveformSampleDto[]? waveforms, DateTimeOffset timestamp, CancellationToken ct = default);

    /// <summary>
    /// Applies the full list of device alarms of one category active on the patient's monitor:
    /// broadcasts AlertTriggered for new / re-prioritised ones and AlertCleared for those gone.
    /// </summary>
    Task IngestDeviceAlarmsAsync(string patientId, AlertCategory category, IReadOnlyList<AlertDto> active, CancellationToken ct = default);

    /// <summary>Registers a device-reported alert. Broadcasts AlertTriggered when it is new or changed severity.</summary>
    Task IngestAlertAsync(AlertDto alert, CancellationToken ct = default);

    Task<AlertDto?> AcknowledgeAlertAsync(string alertId, CancellationToken ct = default);
}

public sealed class MonitorPipeline : IMonitorPipeline
{
    private readonly IHubContext<VitalsHub> _hub;
    private readonly IPatientRegistry _patients;
    private readonly IAlertStateService _alerts;
    private readonly IMonitorBindings _bindings;
    private readonly FeedStatus _feed;
    private readonly ILogger<MonitorPipeline> _logger;

    public MonitorPipeline(
        IHubContext<VitalsHub> hub,
        IPatientRegistry patients,
        IAlertStateService alerts,
        IMonitorBindings bindings,
        FeedStatus feed,
        ILogger<MonitorPipeline> logger)
    {
        _hub = hub;
        _patients = patients;
        _alerts = alerts;
        _bindings = bindings;
        _feed = feed;
        _logger = logger;
    }

    public async Task UpsertPatientAsync(PatientInfoDto patient, CancellationToken ct = default)
    {
        if (_patients.Upsert(patient))
        {
            // PatientAdded doubles as "patient updated": the UI treats it as an upsert by patientId.
            await _hub.Clients.All.SendAsync(HubMethods.PatientAdded, patient, ct);
        }
    }

    public async Task<bool> RemovePatientAsync(string patientId, CancellationToken ct = default)
    {
        if (!_patients.Remove(patientId)) return false;

        _bindings.Forget(patientId);

        foreach (var cleared in _alerts.ClearForPatient(patientId))
        {
            await _hub.Clients.All.SendAsync(HubMethods.AlertCleared, cleared, ct);
        }

        await _hub.Clients.All.SendAsync(HubMethods.PatientRemoved, patientId, ct);
        _logger.LogInformation("Patient {PatientId} removed.", patientId);
        return true;
    }

    public async Task ResetReadingsAsync(string patientId, CancellationToken ct = default)
    {
        _patients.ResetReadings(patientId);

        foreach (var cleared in _alerts.ClearForPatient(patientId))
        {
            await _hub.Clients.All.SendAsync(HubMethods.AlertCleared, cleared, ct);
        }
    }

    public async Task IngestTelemetryAsync(MonitorUpdateDto update, CancellationToken ct = default)
    {
        await UpsertPatientAsync(
            new PatientInfoDto(
                update.PatientId,
                update.PatientName,
                update.BedNumber,
                _patients.Get(update.PatientId)?.Diagnosis,
                _patients.Get(update.PatientId)?.PatientType ?? "Adu"),
            ct);

        if (update.WaveformChannels is { Length: > 0 } channels)
        {
            _patients.UpdateWaveformChannels(update.PatientId, channels);
        }

        await IngestReadingsAsync(
            update.PatientId,
            VitalReadings.From(update.Vitals),
            update.Waveforms,
            update.Timestamp,
            ct);
    }

    public async Task IngestReadingsAsync(
        string patientId,
        VitalReadings readings,
        WaveformSampleDto[]? waveforms,
        DateTimeOffset timestamp,
        CancellationToken ct = default)
    {
        var patient = _patients.Get(patientId);
        if (patient is null)
        {
            _logger.LogDebug("Readings for unknown patient {PatientId} ignored.", patientId);
            return;
        }

        _feed.MarkReceived();
        var merged = _patients.MergeReadings(patientId, readings);

        var update = new MonitorUpdateDto(
            patient.PatientId,
            patient.PatientName,
            patient.BedNumber,
            merged.ToDto(),
            waveforms ?? Array.Empty<WaveformSampleDto>(),
            timestamp,
            _patients.GetWaveformChannels(patientId));

        await _hub.Clients.All.SendAsync(HubMethods.ReceiveVitals, update, ct);

        await BroadcastAsync(_alerts.Reconcile(patientId, merged), ct);
    }

    public async Task IngestDeviceAlarmsAsync(
        string patientId,
        AlertCategory category,
        IReadOnlyList<AlertDto> active,
        CancellationToken ct = default)
    {
        if (_patients.Get(patientId) is null)
        {
            _logger.LogDebug("Device alarms for unknown patient {PatientId} ignored.", patientId);
            return;
        }

        await BroadcastAsync(_alerts.ReconcileDevice(patientId, category, active), ct);
    }

    private async Task BroadcastAsync(AlertChangeSet changes, CancellationToken ct)
    {
        foreach (var raised in changes.Raised)
        {
            await _hub.Clients.All.SendAsync(HubMethods.AlertTriggered, raised, ct);
        }

        foreach (var cleared in changes.Cleared)
        {
            await _hub.Clients.All.SendAsync(HubMethods.AlertCleared, cleared, ct);
        }
    }

    public async Task IngestAlertAsync(AlertDto alert, CancellationToken ct = default)
    {
        var raised = _alerts.Raise(alert);
        if (raised is not null)
        {
            await _hub.Clients.All.SendAsync(HubMethods.AlertTriggered, raised, ct);
        }
    }

    public async Task<AlertDto?> AcknowledgeAlertAsync(string alertId, CancellationToken ct = default)
    {
        var acknowledged = _alerts.Acknowledge(alertId);
        if (acknowledged is not null)
        {
            await _hub.Clients.All.SendAsync(HubMethods.AlertAcknowledged, acknowledged, ct);
        }

        return acknowledged;
    }
}
