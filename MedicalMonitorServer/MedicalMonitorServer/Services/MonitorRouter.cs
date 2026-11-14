using MedicalMonitorServer.Contracts.Api;
using MedicalMonitorServer.Contracts.Models;
using MedicalMonitorServer.Core.Monitoring;

namespace MedicalMonitorServer.Services;

public enum TransferOutcomeKind
{
    Moved,
    AlreadyBound,
    PatientNotFound,
    MonitorNotFound,
}

public sealed record TransferOutcome(TransferOutcomeKind Kind, TransferResultDto? Result = null);

/// <summary>
/// Decides which patient a monitor's data belongs to and keeps the roster in step with the
/// monitor bindings: registers patients a monitor reports, creates an anonymous entry for a
/// monitor that sends values without a patient id, and moves patients between monitors.
/// </summary>
public sealed class MonitorRouter
{
    private readonly IMonitorBindings _bindings;
    private readonly IMonitorPipeline _pipeline;
    private readonly IPatientRegistry _patients;
    private readonly ILogger<MonitorRouter> _logger;

    // Serialises binding changes with the roster updates that follow them, so the feed and
    // the transfer endpoint never interleave.
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Last drop reason logged per monitor; a monitor sends several messages a second.
    private readonly Dictionary<string, string> _loggedDrops = new(StringComparer.Ordinal);

    public MonitorRouter(
        IMonitorBindings bindings,
        IMonitorPipeline pipeline,
        IPatientRegistry patients,
        ILogger<MonitorRouter> logger)
    {
        _bindings = bindings;
        _pipeline = pipeline;
        _patients = patients;
        _logger = logger;
    }

    /// <summary>
    /// Returns the id of the patient a message from <paramref name="monitorIp"/> belongs to,
    /// or null when its values must be dropped.
    /// </summary>
    /// <param name="info">Patient identification from the message; null when it carries none.</param>
    /// <param name="hasReadings">The message carries measured values.</param>
    public async Task<string?> ResolveAsync(string monitorIp, PatientInfo? info, bool hasReadings, CancellationToken ct)
    {
        var reported = info is { PidPresent: true } ? info.PatientId.Trim() : null;
        var bed = info is not null && int.TryParse(info.BedNumber, out var parsed) && parsed > 0 ? parsed : 0;

        await _gate.WaitAsync(ct);
        try
        {
            if (monitorIp.Length == 0)
            {
                // Source unknown: only a message that names its patient can be attributed.
                if (string.IsNullOrEmpty(reported))
                {
                    if (hasReadings) LogDrop("(unknown)", "message has neither a source monitor nor a patient id");
                    return null;
                }

                await UpsertBoundAsync(reported, info, bed, null, ct);
                return reported;
            }

            var result = _bindings.Report(monitorIp, reported, bed, hasReadings, DateTimeOffset.UtcNow);

            if (result.Change is { } change)
            {
                await ApplyAsync(change, info, ct);
            }
            else if (result.PatientId is not null && info is not null)
            {
                await UpsertBoundAsync(result.PatientId, info, bed, null, ct);
            }

            if (result.PatientId is not null)
            {
                _loggedDrops.Remove(monitorIp);
            }
            else if (hasReadings)
            {
                LogDrop(monitorIp, result.DropReason ?? "no patient bound");
            }

            return result.PatientId;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Moves a patient to another monitor by hand.</summary>
    public async Task<TransferOutcome> TransferAsync(string patientId, string monitorIp, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var patient = _patients.Get(patientId);
            if (patient is null) return new TransferOutcome(TransferOutcomeKind.PatientNotFound);

            var result = _bindings.Transfer(patientId, monitorIp);
            switch (result.Status)
            {
                case TransferStatus.MonitorUnknown:
                    return new TransferOutcome(TransferOutcomeKind.MonitorNotFound);

                case TransferStatus.AlreadyBound:
                    return new TransferOutcome(
                        TransferOutcomeKind.AlreadyBound,
                        new TransferResultDto(patient, monitorIp, monitorIp, null));
            }

            var change = result.Change!;
            await ApplyAsync(change, null, ct);

            _logger.LogInformation(
                "Patient {PatientId} transferred by operator to monitor {MonitorIp} (was on {PreviousMonitorIp}).",
                patientId, monitorIp, change.PreviousMonitorIp ?? "none");

            return new TransferOutcome(
                TransferOutcomeKind.Moved,
                new TransferResultDto(
                    _patients.Get(patientId) ?? patient,
                    monitorIp,
                    change.PreviousMonitorIp,
                    change.UnboundPatientId));
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ApplyAsync(BindingChange change, PatientInfo? info, CancellationToken ct)
    {
        string? inheritedDiagnosis = null;
        if (change.SupersededPatientId is { } superseded)
        {
            // The anonymous entry stood in for whoever is on this monitor; they now have a record.
            inheritedDiagnosis = _patients.Get(superseded)?.Diagnosis;
            await _pipeline.RemovePatientAsync(superseded, ct);
        }

        if (change.UnboundPatientId is { } unbound)
        {
            _logger.LogInformation(
                "Patient {PatientId} is no longer on monitor {MonitorIp}; left on the roster without a monitor.",
                unbound, change.MonitorIp);
        }

        if (change.PatientId is null) return;

        if (!change.CreatedAnonymous && _patients.Get(change.PatientId) is not null)
        {
            // Values and alerts from the previous monitor must not carry over to the new one.
            await _pipeline.ResetReadingsAsync(change.PatientId, ct);
        }

        await UpsertBoundAsync(change.PatientId, info, change.BedNumber, inheritedDiagnosis, ct);

        _logger.LogInformation(
            "Monitor {MonitorIp} (bed {BedNumber}) now feeds patient {PatientId}{Origin}.",
            change.MonitorIp,
            change.BedNumber,
            change.PatientId,
            change.CreatedAnonymous ? " (new anonymous entry)"
                : change.PreviousMonitorIp is not null ? $" (moved from monitor {change.PreviousMonitorIp})"
                : string.Empty);
    }

    /// <summary>
    /// Writes the roster entry of the patient bound to a monitor. Only fields the monitor
    /// actually knows are taken from it; diagnosis and anything it left empty are kept.
    /// </summary>
    private Task UpsertBoundAsync(string patientId, PatientInfo? info, int bed, string? inheritedDiagnosis, CancellationToken ct)
    {
        var existing = _patients.Get(patientId);

        // Demographics apply only when the monitor names this very patient; after a manual
        // transfer it may still carry the record of whoever was admitted on it before.
        var named = info is { PidPresent: true } && info.PatientId.Trim() == patientId;
        if (existing is null && !named && !AnonymousPatient.IsAnonymous(patientId)) return Task.CompletedTask;

        var fallbackName = AnonymousPatient.IsAnonymous(patientId) ? AnonymousPatient.DefaultName : patientId;

        return _pipeline.UpsertPatientAsync(
            new PatientInfoDto(
                patientId,
                (named ? MonitorName(info!) : null) ?? existing?.PatientName ?? fallbackName,
                bed > 0 ? bed : existing?.BedNumber ?? 0,
                existing?.Diagnosis ?? inheritedDiagnosis,
                TypeCode(info) ?? existing?.PatientType ?? "Adu"),
            ct);
    }

    private static string? MonitorName(PatientInfo info)
    {
        var name = !string.IsNullOrWhiteSpace(info.FullName)
            ? info.FullName
            : $"{info.LastName} {info.FirstName}".Trim();

        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private static string? TypeCode(PatientInfo? info) => info?.PatientType switch
    {
        PatientType.Adult => "Adu",
        PatientType.Pediatric => "Ped",
        PatientType.Neonate => "Neo",
        _ => null,
    };

    private void LogDrop(string monitorIp, string reason)
    {
        if (_loggedDrops.TryGetValue(monitorIp, out var logged) && logged == reason) return;

        _loggedDrops[monitorIp] = reason;
        _logger.LogInformation("Dropping values from monitor {MonitorIp}: {Reason}.", monitorIp, reason);
    }
}
