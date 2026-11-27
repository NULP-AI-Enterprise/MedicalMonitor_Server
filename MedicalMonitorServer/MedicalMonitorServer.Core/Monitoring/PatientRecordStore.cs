using System.Text.Json;
using MedicalMonitorServer.Contracts.Api;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MedicalMonitorServer.Core.Monitoring;

/// <summary>
/// What a station has recorded about a patient - the bed, the diagnosis, the name given to one
/// their monitor does not name - kept so that it outlives a restart of the server.
/// <para>
/// It is a store of records, not the roster: a patient is on the roster while a monitor reports
/// them (see <see cref="IPatientRegistry"/>). After a restart nobody is on the roster until
/// their monitor says so, and then their record is found here. A patient who was discharged
/// while the server was down therefore does not come back.
/// </para>
/// <para>
/// A monitor that reports no patient id gets an anonymous roster entry under an id made up on
/// the spot; such an entry is not kept. Whoever is on that monitor after a restart may be
/// somebody else, and a stored name must never be put on the wrong patient.
/// </para>
/// </summary>
public interface IPatientRecordStore
{
    /// <summary>The record kept for a patient; null when there is none.</summary>
    PatientInfoDto? Get(string patientId);

    /// <summary>Keeps the patient's record as it is now.</summary>
    void Save(PatientInfoDto patient);

    /// <summary>Drops the record of a patient who has left the roster.</summary>
    void Remove(string patientId);
}

/// <summary>Keeps nothing: every restart starts from what the monitors report.</summary>
public sealed class NoPatientRecordStore : IPatientRecordStore
{
    public PatientInfoDto? Get(string patientId) => null;

    public void Save(PatientInfoDto patient)
    {
    }

    public void Remove(string patientId)
    {
    }
}

/// <summary>
/// The records in one JSON file, rewritten whenever one of them changes: there are as many as
/// there are beds, and they change when somebody edits a patient.
/// </summary>
public sealed class JsonFilePatientRecordStore : IPatientRecordStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly object _lock = new();
    private readonly Dictionary<string, PatientInfoDto> _records = new(StringComparer.Ordinal);
    private readonly string _path;
    private readonly ILogger _logger;

    public JsonFilePatientRecordStore(string path, ILogger<JsonFilePatientRecordStore>? logger = null)
    {
        _path = Path.GetFullPath(path);
        _logger = logger ?? NullLogger<JsonFilePatientRecordStore>.Instance;
        Load();
    }

    public PatientInfoDto? Get(string patientId)
    {
        lock (_lock)
        {
            return _records.TryGetValue(patientId, out var record) ? record : null;
        }
    }

    public void Save(PatientInfoDto patient)
    {
        if (AnonymousPatient.IsAnonymous(patient.PatientId)) return;

        // A patient a station admitted is kept in the history database, with the monitor they
        // are on, and is put back from there after a restart. Kept here as well, there would
        // be two records of them to disagree.
        if (patient.AdmittedAt is not null) return;

        lock (_lock)
        {
            if (_records.TryGetValue(patient.PatientId, out var kept) && kept == patient) return;

            _records[patient.PatientId] = patient;
            Write();
        }
    }

    public void Remove(string patientId)
    {
        lock (_lock)
        {
            if (_records.Remove(patientId)) Write();
        }
    }

    private void Load()
    {
        if (!File.Exists(_path)) return;

        try
        {
            var records = JsonSerializer.Deserialize<List<PatientInfoDto>>(File.ReadAllText(_path), Json) ?? new();
            foreach (var record in records.Where(r => !string.IsNullOrWhiteSpace(r.PatientId)))
            {
                _records[record.PatientId] = record;
            }

            _logger.LogInformation("Loaded {Count} patient record(s) from {Path}.", _records.Count, _path);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // The server must come up without it: the monitors still say who is on them. The
            // file is put aside, not overwritten, so that what was in it can be looked at.
            var aside = $"{_path}.unreadable-{DateTime.Now:yyyyMMdd-HHmmss}";
            _logger.LogError(ex, "Patient records in {Path} could not be read; starting without them. The file is kept as {Aside}.", _path, aside);
            try
            {
                File.Move(_path, aside, overwrite: true);
            }
            catch (Exception moveError) when (moveError is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(moveError, "{Path} could not be put aside either; it will be overwritten by the next change.", _path);
            }
        }
    }

    /// <summary>Called under <see cref="_lock"/>.</summary>
    private void Write()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

            // Written beside the file and moved over it, so that a power cut in the middle
            // leaves either the old records or the new ones, never half a file.
            var temp = _path + ".tmp";
            var ordered = _records.Values.OrderBy(r => r.BedNumber).ThenBy(r => r.PatientId, StringComparer.Ordinal).ToList();
            File.WriteAllText(temp, JsonSerializer.Serialize(ordered, Json));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Monitoring goes on; what is lost is that this change survives a restart.
            _logger.LogError(ex, "Patient records could not be written to {Path}.", _path);
        }
    }
}
