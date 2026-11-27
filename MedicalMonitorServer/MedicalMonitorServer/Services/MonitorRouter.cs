using MedicalMonitorServer.Contracts.Api;
using MedicalMonitorServer.Contracts.Models;
using MedicalMonitorServer.Core.Monitoring;
using MedicalMonitorServer.Dal.History;
using MedicalMonitorServer.DeviceDriver;

namespace MedicalMonitorServer.Services;

public enum TransferOutcomeKind
{
    Moved,
    AlreadyBound,
    PatientNotFound,
    MonitorNotFound,
}

public sealed record TransferOutcome(TransferOutcomeKind Kind, TransferResultDto? Result = null);

/// <summary>What a station enters about a patient, when admitting them or afterwards.</summary>
/// <param name="DrAlexId">The patient's id in the hospital's DrAlex system; null when none was typed.</param>
/// <param name="PatientName">Empty when none was typed.</param>
public sealed record PatientEntry(string? DrAlexId, string PatientName, int BedNumber, string PatientType, string? Diagnosis);

public enum AdmitOutcomeKind
{
    Admitted,

    /// <summary>Nobody on the roster has this id.</summary>
    PatientNotFound,

    AlreadyAdmitted,

    /// <summary>The patient is on no monitor, so there is nothing to record.</summary>
    NoMonitor,

    /// <summary>The history database is off or could not be written; nobody was admitted.</summary>
    HistoryUnavailable,

    /// <summary>An earlier patient was to be admitted again, and nobody was ever admitted here under that id.</summary>
    EarlierPatientNotFound,

    /// <summary>An earlier patient was to be admitted again, and they are admitted now, on another monitor.</summary>
    EarlierPatientAdmitted,
}

/// <param name="Patient">
/// The admitted patient - under the id made up for them, or the one they had - when
/// <see cref="AdmitOutcomeKind.Admitted"/>. When <see cref="AdmitOutcomeKind.EarlierPatientAdmitted"/>,
/// the patient as they are admitted now: which bed they are to be discharged from first.
/// </param>
public sealed record AdmitOutcome(AdmitOutcomeKind Kind, PatientInfoDto? Patient = null);

public enum DischargeOutcome
{
    Discharged,
    PatientNotFound,

    /// <summary>The end of the recording could not be written; the patient stays admitted.</summary>
    HistoryUnavailable,
}

public enum EditOutcomeKind
{
    Updated,
    PatientNotFound,

    /// <summary>The change could not be written to the history database; nothing was changed.</summary>
    HistoryUnavailable,
}

public sealed record EditOutcome(EditOutcomeKind Kind, PatientInfoDto? Patient = null);

/// <summary>
/// Decides which patient a monitor's data belongs to and keeps the roster in step with the
/// monitor bindings: registers patients a monitor reports, creates an anonymous entry for a
/// monitor that sends values without a patient id, and moves patients between monitors.
/// <para>
/// A patient a station admitted (<see cref="AdmitAsync"/>, or <see cref="ReadmitAsync"/> for
/// somebody who was here before) is another matter: the station says who they are and when
/// they leave (<see cref="DischargeAsync"/>), their values are recorded in between, and
/// nothing their monitor reports about its own patient changes that.
/// </para>
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

    private readonly IMonitorAdmissions? _admissions;
    private readonly IPatientRecordStore _records;
    private readonly PatientHistory? _history;

    /// <param name="admissions">Writes the patient to monitors that take it; without one no monitor is told anything.</param>
    /// <param name="records">Patient records kept across restarts; nothing is kept when omitted.</param>
    /// <param name="history">Where admitted patients are recorded; without one nobody can be admitted.</param>
    public MonitorRouter(
        IMonitorBindings bindings,
        IMonitorPipeline pipeline,
        IPatientRegistry patients,
        ILogger<MonitorRouter> logger,
        IMonitorAdmissions? admissions = null,
        IPatientRecordStore? records = null,
        PatientHistory? history = null)
    {
        _bindings = bindings;
        _pipeline = pipeline;
        _patients = patients;
        _admissions = admissions;
        _records = records ?? new NoPatientRecordStore();
        _history = history;
        _logger = logger;
    }

    /// <summary>
    /// Tells the monitor itself who its patient is, where the monitor can be told (see
    /// <see cref="IMonitorAdmissions"/>). The binding made on the server does not depend on it:
    /// a monitor that refuses or is off keeps feeding the patient it was given by hand.
    /// </summary>
    private async Task WriteToMonitorAsync(string monitorIp, PatientInfoDto patient, bool nameOnly, CancellationToken ct)
    {
        if (_admissions is null) return;

        MonitorAdmissionResult result;
        try
        {
            result = nameOnly
                ? await _admissions.UpdatePatientAsync(monitorIp, patient.PatientId, patient.PatientName, ct)
                : await _admissions.AssignPatientAsync(monitorIp, patient.PatientId, patient.PatientName, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Writing patient {PatientId} to monitor {MonitorIp} failed.", patient.PatientId, monitorIp);
            return;
        }

        switch (result.Status)
        {
            case MonitorAdmissionStatus.NotSupported:
                return;

            case MonitorAdmissionStatus.Accepted:
                _logger.LogInformation("Monitor {MonitorIp} now carries patient {PatientId} ({Detail}).",
                    monitorIp, patient.PatientId, result.Detail);
                return;

            default:
                _logger.LogWarning(
                    "Patient {PatientId} was not written to monitor {MonitorIp}: {Status} ({Detail}). " +
                    "The monitor keeps reporting what it had; enter the patient on the monitor by hand.",
                    patient.PatientId, monitorIp, result.Status, result.Detail);
                return;
        }
    }

    /// <summary>
    /// After a patient's roster entry was edited: passes the new name to the monitor they are on.
    /// Not for a patient a station admitted: the id they have here is not one a monitor could
    /// carry, and the monitor's own record of its patient is left to the bedside.
    /// </summary>
    public Task PatientEditedAsync(PatientInfoDto patient, CancellationToken ct) =>
        patient.AdmittedAt is null && _bindings.FindMonitorOf(patient.PatientId) is { } monitorIp
            ? WriteToMonitorAsync(monitorIp, patient, nameOnly: true, ct)
            : Task.CompletedTask;

    /// <summary>
    /// Admits whoever is on a monitor: from now on they are a patient of the station's, under
    /// an id made up here, and their values are recorded until a station discharges them.
    /// </summary>
    /// <param name="patientId">
    /// The roster entry that stands for the monitor's patient now - the id the monitor reports,
    /// or an anonymous one. It is replaced by the admitted patient.
    /// </param>
    public Task<AdmitOutcome> AdmitAsync(string patientId, PatientEntry entry, CancellationToken ct) =>
        AdmitOnMonitorAsync(
            patientId,
            (history, monitorIp, now, standingCuff) =>
            {
                var admitted = new PatientInfoDto(
                    Guid.NewGuid().ToString(),
                    entry.PatientName,
                    entry.BedNumber,
                    entry.Diagnosis,
                    entry.PatientType,
                    entry.DrAlexId,
                    now);

                history.Admit(
                    new NewRecordedPatient(
                        admitted.PatientId,
                        admitted.DrAlexId,
                        admitted.PatientName.Length > 0 ? admitted.PatientName : null,
                        admitted.BedNumber,
                        admitted.PatientType,
                        admitted.Diagnosis,
                        monitorIp,
                        now,
                        standingCuff));

                return new AdmitOutcome(AdmitOutcomeKind.Admitted, admitted);
            },
            again: false,
            ct);

    /// <summary>
    /// Admits whoever is on a monitor as a patient who was here before and has been discharged:
    /// they come back under the id they had, with what was entered about them then, and their
    /// values are recorded into the same history, after a gap for the time they were away.
    /// </summary>
    /// <param name="patientId">The roster entry that stands for the monitor's patient now, as in <see cref="AdmitAsync"/>.</param>
    /// <param name="earlierPatientId">The id the patient was given at their first admission.</param>
    /// <param name="bedNumber">The bed they lie on this time.</param>
    public Task<AdmitOutcome> ReadmitAsync(string patientId, string earlierPatientId, int bedNumber, CancellationToken ct) =>
        AdmitOnMonitorAsync(
            patientId,
            (history, monitorIp, now, standingCuff) =>
            {
                var earlier = history.GetPatient(earlierPatientId);
                if (earlier is null) return new AdmitOutcome(AdmitOutcomeKind.EarlierPatientNotFound);

                // One person lies in one bed: they are discharged from the other one first.
                var readmitted = earlier.DischargedAt is null
                    ? null
                    : history.Readmit(earlier.Id, monitorIp, bedNumber, now, standingCuff);

                return readmitted is null
                    ? new AdmitOutcome(AdmitOutcomeKind.EarlierPatientAdmitted, PatientHistory.ToRosterEntry(earlier))
                    : new AdmitOutcome(AdmitOutcomeKind.Admitted, PatientHistory.ToRosterEntry(readmitted));
            },
            again: true,
            ct);

    /// <summary>
    /// Writes an admission down in the history. Answers with the patient as they are admitted,
    /// or with why nobody was; throws when the database cannot be read or written.
    /// </summary>
    /// <param name="standingCuff">The cuff reading the monitor shows at this moment.</param>
    private delegate AdmitOutcome WriteAdmission(PatientHistory history, string monitorIp, DateTimeOffset now, CuffReading? standingCuff);

    /// <summary>
    /// What a first admission and a later one have in common: the roster entry of whoever is on
    /// the monitor gives way to the admitted patient, who is held on that monitor from then on.
    /// </summary>
    /// <param name="again">For the log: the patient was here before.</param>
    private async Task<AdmitOutcome> AdmitOnMonitorAsync(string patientId, WriteAdmission writeDown, bool again, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var current = _patients.Get(patientId);
            if (current is null) return new AdmitOutcome(AdmitOutcomeKind.PatientNotFound);
            if (current.AdmittedAt is not null) return new AdmitOutcome(AdmitOutcomeKind.AlreadyAdmitted);

            // What is recorded is what a monitor measures: somebody on no monitor has nothing to record.
            if (_bindings.FindMonitorOf(patientId) is not { } monitorIp) return new AdmitOutcome(AdmitOutcomeKind.NoMonitor);
            if (_history is not { IsOn: true }) return new AdmitOutcome(AdmitOutcomeKind.HistoryUnavailable);

            // To the second, as the database keeps it: the entry must read the same after a restart.
            var now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

            var standing = _patients.GetReadings(patientId);
            var channels = _patients.GetWaveformChannels(patientId);

            // The database first: an admission that is not written down is not one. The cuff
            // reading on the monitor at this moment was taken before it, of whoever - what is
            // recorded of this patient from now on begins with the next.
            AdmitOutcome written;
            try
            {
                written = writeDown(_history, monitorIp, now, VitalsRecorder.CuffOf(standing));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex,
                    "The patient on monitor {MonitorIp} was not admitted: the history database could not be written.", monitorIp);
                return new AdmitOutcome(AdmitOutcomeKind.HistoryUnavailable);
            }

            if (written is not { Kind: AdmitOutcomeKind.Admitted, Patient: { } admitted }) return written;

            try
            {
                _bindings.Hold(admitted.PatientId, monitorIp, admitted.BedNumber, DateTimeOffset.UtcNow);

                // The entry that stood for the monitor's patient is this patient; it goes.
                await _pipeline.RemovePatientAsync(patientId, ct);
                await _pipeline.UpsertPatientAsync(admitted, ct);

                // Same monitor, same person: what it measures right now is theirs to show.
                if (channels.Length > 0) _patients.UpdateWaveformChannels(admitted.PatientId, channels);
                if (standing is not null) _patients.MergeReadings(admitted.PatientId, standing);
            }
            catch
            {
                // Half an admission must not come back as a whole one after a restart. For
                // somebody admitted again that leaves a stay that ended the second it began.
                try
                {
                    _history.Discharge(admitted.PatientId, now);
                }
                catch (Exception undo) when (undo is not OperationCanceledException)
                {
                    _logger.LogError(undo, "The unfinished admission of {PatientId} could not be taken back.", admitted.PatientId);
                }

                _bindings.Forget(admitted.PatientId);
                throw;
            }

            if (again)
            {
                _logger.LogInformation(
                    "Patient {PatientId} (DrAlex id {DrAlexId}) admitted again by a station on monitor {MonitorIp}, bed {BedNumber}, in place of {Replaced}; recording goes on in the history they have.",
                    admitted.PatientId, admitted.DrAlexId ?? "none", monitorIp, admitted.BedNumber, patientId);
            }
            else
            {
                _logger.LogInformation(
                    "Patient {PatientId} (DrAlex id {DrAlexId}) admitted by a station on monitor {MonitorIp}, bed {BedNumber}, in place of {Replaced}; recording started.",
                    admitted.PatientId, admitted.DrAlexId ?? "none", monitorIp, admitted.BedNumber, patientId);
            }

            return written;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Takes a patient off the roster at a station's word. For an admitted patient that ends
    /// the recording: their monitor, if it keeps sending, shows up as somebody not admitted,
    /// until a station admits the next patient there - or this one again, into the history they
    /// have (<see cref="ReadmitAsync"/>). A patient no station admitted comes back as long as
    /// their monitor names them.
    /// </summary>
    public async Task<DischargeOutcome> DischargeAsync(string patientId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var patient = _patients.Get(patientId);
            if (patient is null) return DischargeOutcome.PatientNotFound;

            if (patient.AdmittedAt is not null)
            {
                // The database first: a patient taken off the roster but still admitted in it
                // would be put back after a restart, onto whoever is on the monitor by then.
                if (_history is not { IsOn: true }) return DischargeOutcome.HistoryUnavailable;

                try
                {
                    _history.Discharge(patientId, DateTimeOffset.UtcNow);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex,
                        "Patient {PatientId} was not discharged: the history database could not be written.", patientId);
                    return DischargeOutcome.HistoryUnavailable;
                }

                _logger.LogInformation("Patient {PatientId} discharged by a station; recording ended.", patientId);
            }

            await _pipeline.RemovePatientAsync(patientId, ct);
            return DischargeOutcome.Discharged;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Replaces what a station entered about a patient, admitted or not.</summary>
    public async Task<EditOutcome> EditAsync(string patientId, PatientEntry entry, CancellationToken ct)
    {
        PatientInfoDto updated;

        await _gate.WaitAsync(ct);
        try
        {
            var existing = _patients.Get(patientId);
            if (existing is null) return new EditOutcome(EditOutcomeKind.PatientNotFound);

            updated = existing with
            {
                PatientName = entry.PatientName,
                BedNumber = entry.BedNumber,
                Diagnosis = entry.Diagnosis,
                PatientType = entry.PatientType,
                // Typing it in is part of admitting somebody; it does not admit them.
                DrAlexId = existing.AdmittedAt is null ? null : entry.DrAlexId,
            };

            if (existing.AdmittedAt is not null && updated != existing)
            {
                // As with admission, the database first: otherwise the next restart would
                // bring the old entry back.
                if (_history is not { IsOn: true }) return new EditOutcome(EditOutcomeKind.HistoryUnavailable);

                try
                {
                    _history.Update(updated);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex,
                        "Patient {PatientId} was not changed: the history database could not be written.", patientId);
                    return new EditOutcome(EditOutcomeKind.HistoryUnavailable);
                }
            }

            await _pipeline.UpsertPatientAsync(updated, ct);
        }
        finally
        {
            _gate.Release();
        }

        // Outside the gate: a monitor that is off takes seconds to give up on.
        await PatientEditedAsync(updated, ct);
        return new EditOutcome(EditOutcomeKind.Updated, updated);
    }

    /// <summary>
    /// After a restart: puts the patients who were admitted and not discharged back on the
    /// roster and on their monitors, recording again. Run before the feed starts, so that a
    /// monitor's first message finds its patient there.
    /// </summary>
    public async Task RestoreAsync(CancellationToken ct)
    {
        if (_history is null) return;

        await _gate.WaitAsync(ct);
        try
        {
            var patients = _history.Resume();
            foreach (var recorded in patients)
            {
                var change = _bindings.Hold(recorded.Id, recorded.MonitorIp, recorded.BedNumber, DateTimeOffset.UtcNow);

                // Only when a monitor was heard before this ran: somebody is standing in for the patient.
                if (change?.SupersededPatientId is { } superseded) await _pipeline.RemovePatientAsync(superseded, ct);
                if (change?.UnboundPatientId is { } unbound) await _pipeline.RemovePatientAsync(unbound, ct);

                await _pipeline.UpsertPatientAsync(PatientHistory.ToRosterEntry(recorded), ct);
            }

            if (patients.Count > 0)
            {
                _logger.LogInformation(
                    "Put {Count} admitted patient(s) back on their monitors: {Patients}.",
                    patients.Count,
                    string.Join(", ", patients.Select(p => $"{p.Id} on {p.MonitorIp ?? "no monitor"}")));
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The monitor of an admitted patient names somebody else, or nobody: the patient stays and
    /// goes on being recorded, and the stations are told, because only a person can say whether
    /// the patient on the monitor is still the one who was admitted.
    /// </summary>
    private Task MonitorNamesAnotherAsync(string monitorIp, string patientId, ReportedIdChange renamed, CancellationToken ct)
    {
        // A monitor that had no id and now has one was, most likely, just told the name of the
        // patient it already had; what it reported before a restart is not known at all.
        if (string.IsNullOrEmpty(renamed.Before)) return Task.CompletedTask;

        var message = renamed.After.Length > 0
            ? $"Monitor reports another patient: {renamed.After}"
            : "Monitor reports its patient discharged";

        _logger.LogWarning(
            "Monitor {MonitorIp} reported patient {Before} and now reports {After}, while {PatientId}, admitted by a station, is on it. " +
            "The recording goes on under {PatientId}; discharge them at a station if the patient has changed.",
            monitorIp, renamed.Before, renamed.After.Length > 0 ? renamed.After : "none", patientId, patientId);

        // A code, so that the threshold check does not take it for one of its own and clear it;
        // no category, so that the monitor's own alarm lists do not either. It stands until the
        // patient is discharged.
        return _pipeline.IngestAlertAsync(
            new AlertDto(
                AlertDto.BuildMonitorPatientChangedId(patientId),
                patientId,
                VitalSign: null,
                AlertDto.SeverityOf(DeviceAlarmLevel.Low),
                message,
                Value: null,
                DateTimeOffset.UtcNow,
                Source: AlertSource.Device,
                Code: AlertDto.MonitorPatientChangedCode,
                Level: DeviceAlarmLevel.Low),
            ct);
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
                await ApplyAsync(change, info, byMonitor: true, ct);
            }
            else if (result.PatientId is not null && info is not null)
            {
                await UpsertBoundAsync(result.PatientId, info, bed, null, ct);
            }

            if (result is { Renamed: { } renamed, PatientId: { } held })
            {
                await MonitorNamesAnotherAsync(monitorIp, held, renamed, ct);
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

    /// <summary>
    /// Flags the patients whose monitor has sent nothing for longer than
    /// <paramref name="silentAfter"/>, and unflags those whose monitor is talking again.
    /// </summary>
    /// <remarks>
    /// Runs under the same gate as <see cref="ResolveAsync"/>, which is what stamps a
    /// monitor's last-seen time. So a message either was resolved before this looked — and
    /// its monitor is not silent — or is resolved after, and its values are ingested after
    /// the snapshot was dropped. A live bed can never be blanked by a message arriving at
    /// the same instant.
    /// "Sent nothing" means no message at all, the monitor's once-a-second echo included: a
    /// monitor that still echoes but reports no vitals is not silent, its values are simply
    /// absent. Patients fed only over REST have no monitor binding and are not covered.
    /// </remarks>
    public async Task SweepSilentAsync(DateTimeOffset now, TimeSpan silentAfter, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var silent = new Dictionary<string, MonitorBindingDto>(StringComparer.Ordinal);
            foreach (var monitor in _bindings.GetAll())
            {
                if (monitor.PatientId is { } patientId && now - monitor.LastSeen > silentAfter)
                {
                    silent.TryAdd(patientId, monitor);
                }
            }

            await _pipeline.ReconcileMonitorSilenceAsync(silent, now, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Moves a patient to another monitor by hand, and tells the monitor where it can be told.</summary>
    public async Task<TransferOutcome> TransferAsync(string patientId, string monitorIp, CancellationToken ct)
    {
        var outcome = await BindAsync(patientId, monitorIp, ct);

        // Outside the gate: a monitor that is off takes seconds to give up on, and the feed
        // must not wait for it. Not for a patient a station admitted: see PatientEditedAsync.
        if (outcome is { Kind: TransferOutcomeKind.Moved, Result: { Patient.AdmittedAt: null } moved })
        {
            await WriteToMonitorAsync(monitorIp, moved.Patient, nameOnly: false, ct);
        }

        return outcome;
    }

    private async Task<TransferOutcome> BindAsync(string patientId, string monitorIp, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var patient = _patients.Get(patientId);
            if (patient is null) return new TransferOutcome(TransferOutcomeKind.PatientNotFound);

            // For the history of an admitted patient: the cuff reading the target monitor shows
            // now was taken of whoever is on it, not of the patient about to be.
            var standingCuff = patient.AdmittedAt is not null && _bindings.Get(monitorIp)?.PatientId is { } occupant
                ? VitalsRecorder.CuffOf(_patients.GetReadings(occupant))
                : null;

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
            await ApplyAsync(change, null, byMonitor: false, ct);

            if (patient.AdmittedAt is not null) NoteMove(patientId, monitorIp, change.UnboundPatientId, standingCuff);

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

    /// <summary>
    /// Writes down which monitor an admitted patient is on after a transfer, so that a restart
    /// puts them back there. A failure is logged and no more: the patient is on the new
    /// monitor now, whatever the database says.
    /// </summary>
    private void NoteMove(string patientId, string monitorIp, string? displacedPatientId, CuffReading? standingCuff)
    {
        if (_history is not { IsOn: true }) return;

        // Somebody admitted who was on that monitor waits for one now, and is recorded as such.
        var displaced = displacedPatientId is not null && _patients.Get(displacedPatientId)?.AdmittedAt is not null
            ? displacedPatientId
            : null;

        try
        {
            _history.Move(patientId, monitorIp, displaced, standingCuff);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex,
                "The history database could not be told that patient {PatientId} is on monitor {MonitorIp} now; " +
                "after a restart they would be put back where they were.",
                patientId, monitorIp);
        }
    }

    /// <param name="byMonitor">
    /// The change is what the monitor itself reported, not an operator's transfer. It decides
    /// what becomes of a patient who lost the monitor: see below.
    /// </param>
    private async Task ApplyAsync(BindingChange change, PatientInfo? info, bool byMonitor, CancellationToken ct)
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
            if (byMonitor)
            {
                // The monitor no longer has this patient: they were discharged at the bedside, or
                // the next one was admitted in their place. A patient is discharged on the
                // monitor and nowhere else, so this is where they leave the roster too.
                _logger.LogInformation(
                    "Patient {PatientId} was discharged on monitor {MonitorIp}; removed from the roster.",
                    unbound, change.MonitorIp);
                await _pipeline.RemovePatientAsync(unbound, ct);
            }
            else
            {
                // An operator put somebody else on the monitor. The patient who was on it is
                // still in the unit and is waiting for a monitor of their own.
                _logger.LogInformation(
                    "Patient {PatientId} is no longer on monitor {MonitorIp}; left on the roster without a monitor.",
                    unbound, change.MonitorIp);
            }
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
        // On the roster already, or known from before a restart: the bed, the diagnosis and a
        // name the monitor does not give are then taken from the record that was kept.
        var existing = _patients.Get(patientId) ?? _records.Get(patientId);

        // A patient a station admitted is who the station says they are. The monitor's record
        // is of whoever it was told about at the bedside, which need not be them, and nothing
        // in it - name, bed, type - may overwrite what was entered here.
        if (existing?.AdmittedAt is not null) return Task.CompletedTask;

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
