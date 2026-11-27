using MedicalMonitorServer.Contracts.Api;
using MedicalMonitorServer.Core.Monitoring;
using MedicalMonitorServer.Dal.History;

namespace MedicalMonitorServer.Services;

/// <summary>
/// The recording of the patients a station admitted: the database, the recorder that says what
/// goes into it each second, and whether it is working. Admission and discharge go through
/// here so that a patient is in the database exactly while the recorder has them.
/// <para>
/// A failure of the database never stops the monitoring. It stops the recording, and that is
/// said out loud - in <see cref="Status"/>, which /api/v1/health passes on to the stations -
/// because a bed that says it is recorded while nothing is written is worse than no recording.
/// </para>
/// </summary>
public sealed class PatientHistory : IDisposable
{
    public const string Off = "Off";
    public const string Recording = "Recording";
    public const string Failing = "Failing";

    /// <summary>One second that could not be written may be a hiccup; this many in a row is a database that is not there.</summary>
    private const int FailuresBeforeFailing = 3;

    private static readonly TimeSpan FailureLogInterval = TimeSpan.FromMinutes(1);

    private readonly IHistoryStore? _store;
    private readonly IVitalsRecorder _recorder;
    private readonly ILogger<PatientHistory> _logger;
    private readonly bool _configured;

    // Touched by the one-second loop only.
    private int _failedWrites;
    private DateTimeOffset _failureLoggedAt;

    /// <param name="store">The database; null when there is none - not set up, or it could not be opened.</param>
    /// <param name="configured">A database was asked for (History:Path). Without a store that makes it failing, not off.</param>
    public PatientHistory(IHistoryStore? store, IVitalsRecorder recorder, ILogger<PatientHistory> logger, bool configured = true)
    {
        _store = store;
        _recorder = recorder;
        _logger = logger;
        _configured = configured;
    }

    /// <summary>
    /// Opens the database at <paramref name="path"/> (relative paths are under
    /// <paramref name="contentRoot"/>). An empty path turns the history off; a database that
    /// cannot be opened leaves the server running without one, and says so.
    /// </summary>
    public static PatientHistory Open(string? path, string contentRoot, IVitalsRecorder recorder, ILogger<PatientHistory> logger)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            logger.LogInformation("History:Path is empty: admitted patients' values are not recorded.");
            return new PatientHistory(null, recorder, logger, configured: false);
        }

        var file = Path.GetFullPath(Path.Combine(contentRoot, path));
        try
        {
            var store = new SqliteHistoryStore(file);
            if (store.PutAside is { } aside)
            {
                logger.LogError(
                    "The history database {Path} could not be read and was put aside as {Aside}; a new one was started.",
                    file, aside);
            }

            logger.LogInformation(
                "Recording admitted patients' values in {Path} ({Size:0.0} MB).", file, store.SizeBytes / 1048576.0);
            return new PatientHistory(store, recorder, logger);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The history database {Path} could not be opened; nothing will be recorded.", file);
            return new PatientHistory(null, recorder, logger);
        }
    }

    /// <summary>There is a database to write to. Whether the last writes went through is in <see cref="Status"/>.</summary>
    public bool IsOn => _store is not null;

    /// <summary><see cref="Off"/>, <see cref="Recording"/> or <see cref="Failing"/>.</summary>
    public string Status =>
        !_configured ? Off
        : _store is null || Volatile.Read(ref _failedWrites) >= FailuresBeforeFailing ? Failing
        : Recording;

    private IHistoryStore Store =>
        _store ?? throw new InvalidOperationException("There is no history database on this server.");

    /// <summary>
    /// Puts a newly admitted patient in the database and starts recording them. Throws when the
    /// database cannot be written. The cuff reading on the monitor at this moment
    /// (<see cref="NewRecordedPatient.StandingCuff"/>) was taken before the admission, so it is
    /// not theirs: it is kept with the patient, and never recorded.
    /// </summary>
    public RecordedPatient Admit(NewRecordedPatient patient)
    {
        var recorded = Store.Admit(patient);
        _recorder.Start(recorded.Id, recorded.Num, patient.StandingCuff);
        return recorded;
    }

    /// <summary>
    /// Admits a discharged patient again and goes on recording them under the number they
    /// have, so the new values continue their history. Null, and nothing changed, when nobody
    /// has this id or they are admitted now. Throws when the database cannot be written.
    /// As at a first admission, the cuff reading on the monitor at this moment is kept with
    /// the patient and never recorded: it was taken while nobody was admitted there.
    /// </summary>
    public RecordedPatient? Readmit(string patientId, string monitorIp, int bedNumber, DateTimeOffset at, CuffReading? standingCuff)
    {
        var recorded = Store.Readmit(patientId, monitorIp, bedNumber, at, standingCuff);
        if (recorded is not null) _recorder.Start(recorded.Id, recorded.Num, standingCuff);
        return recorded;
    }

    /// <summary>Ends a patient's recording. Throws when the database cannot be written: the patient then stays admitted.</summary>
    public void Discharge(string patientId, DateTimeOffset at)
    {
        Store.Discharge(patientId, at);
        _recorder.Stop(patientId);
    }

    /// <summary>Keeps what a station changed about an admitted patient. Throws when the database cannot be written.</summary>
    public void Update(PatientInfoDto patient) =>
        Store.Update(
            patient.PatientId,
            patient.DrAlexId,
            patient.PatientName.Length > 0 ? patient.PatientName : null,
            patient.BedNumber,
            patient.PatientType,
            patient.Diagnosis);

    /// <summary>An admitted patient was moved to another monitor. Throws when the database cannot be written.</summary>
    /// <param name="displacedId">The admitted patient who was on that monitor and now has none.</param>
    /// <param name="standingCuff">The cuff reading the new monitor already shows.</param>
    public void Move(string patientId, string? monitorIp, string? displacedId, CuffReading? standingCuff)
    {
        Store.MoveMonitor(patientId, monitorIp, displacedId, standingCuff, DateTimeOffset.UtcNow);
        _recorder.Rebase(patientId, standingCuff);
    }

    /// <summary>
    /// The cuff reading a patient's monitor is expected to be showing, which is not to be
    /// recorded again: the last one recorded for them, or - until there is one from the monitor
    /// they are on now - the one that stood on it when they came.
    /// </summary>
    private CuffReading? StandingCuffOf(RecordedPatient patient)
    {
        var last = Store.GetLastCuff(patient.Num);
        if (last is null) return patient.StandingCuff;

        // Moved to another monitor since their last reading: what that monitor showed stands,
        // even when it showed nothing.
        return patient.StandingSince is { } since && since > last.Timestamp ? patient.StandingCuff : last.Reading;
    }

    /// <summary>
    /// After a restart: everybody who was admitted and not discharged, recording again. Empty
    /// when there is no database.
    /// </summary>
    public IReadOnlyList<RecordedPatient> Resume()
    {
        if (_store is null) return Array.Empty<RecordedPatient>();

        var resumed = new List<RecordedPatient>();
        var monitors = new HashSet<string>(StringComparer.Ordinal);

        // Newest admission first: should two be on one monitor - it cannot happen through the
        // stations, but the file can be edited - the later one is who is there now.
        foreach (var patient in _store.GetActive().OrderByDescending(p => p.AdmittedAt).ThenByDescending(p => p.Num))
        {
            if (patient.MonitorIp is { } monitorIp && !monitors.Add(monitorIp))
            {
                _logger.LogWarning(
                    "Patient {PatientId} was still admitted on monitor {MonitorIp}, where a later patient is; discharged.",
                    patient.Id, monitorIp);
                _store.Discharge(patient.Id, DateTimeOffset.UtcNow);
                continue;
            }

            _recorder.Start(patient.Id, patient.Num, StandingCuffOf(patient));
            resumed.Add(patient);
        }

        resumed.Reverse();
        return resumed;
    }

    /// <summary>Writes the rows of the second that just passed. Never throws.</summary>
    public void Flush(DateTimeOffset now)
    {
        if (_store is null) return;

        try
        {
            _store.Append(_recorder.Drain(now));

            if (_failedWrites > 0)
            {
                _logger.LogInformation("The history database is being written again after {Count} failed second(s).", _failedWrites);
                Volatile.Write(ref _failedWrites, 0);
            }
        }
        catch (Exception ex)
        {
            // The rows of this second are lost: holding them back would only pile them up
            // behind a database that is not there.
            Volatile.Write(ref _failedWrites, _failedWrites + 1);
            if (now - _failureLoggedAt >= FailureLogInterval)
            {
                _failureLoggedAt = now;
                _logger.LogError(ex, "The history database could not be written; the values of this second are lost.");
            }
        }
    }

    /// <summary>One line with the size of the database, for the log.</summary>
    public void LogSize()
    {
        if (_store is null) return;

        try
        {
            _logger.LogInformation("The history database holds {Size:0.0} MB.", _store.SizeBytes / 1048576.0);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Recorded patients, the last admitted first. Throws when there is no database.</summary>
    public IReadOnlyList<RecordedPatient> GetPatients(string? drAlexId, int limit) => Store.GetPatients(drAlexId, limit);

    /// <summary>Discharged patients, the last discharged first: who a station may admit again. Throws when there is no database.</summary>
    public IReadOnlyList<RecordedPatient> GetDischarged(string? drAlexId, int limit) => Store.GetDischarged(drAlexId, limit);

    public RecordedPatient? GetPatient(string patientId) => Store.GetPatient(patientId);

    /// <summary>The stays of these patients, by <see cref="RecordedPatient.Num"/>, each in the order they happened.</summary>
    public IReadOnlyDictionary<int, IReadOnlyList<RecordedAdmission>> GetAdmissions(IReadOnlyCollection<int> patientNums) =>
        Store.GetAdmissions(patientNums);

    public IReadOnlyList<VitalsRow> GetVitals(int patientNum, long from, long to, int limit) =>
        Store.GetVitals(patientNum, from, to, limit);

    /// <summary>The roster entry of a patient read back from the database.</summary>
    public static PatientInfoDto ToRosterEntry(RecordedPatient patient) => new(
        patient.Id,
        patient.FullName ?? string.Empty,
        patient.BedNumber,
        patient.Diagnosis,
        patient.PatientType,
        patient.DrAlexId,
        patient.AdmittedAt);

    public void Dispose() => _store?.Dispose();
}
