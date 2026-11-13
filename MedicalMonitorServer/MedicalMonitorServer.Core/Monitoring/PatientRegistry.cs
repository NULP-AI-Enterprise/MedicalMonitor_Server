using System.Collections.Concurrent;
using MedicalMonitorServer.Contracts.Api;

namespace MedicalMonitorServer.Core.Monitoring;

/// <summary>In-memory roster of monitored patients and their latest readings.</summary>
public interface IPatientRegistry
{
    int Count { get; }

    IReadOnlyList<PatientInfoDto> GetAll();

    PatientInfoDto? Get(string patientId);

    /// <summary>Adds or replaces a patient. Returns true when the stored data actually changed.</summary>
    bool Upsert(PatientInfoDto patient);

    bool Remove(string patientId);

    VitalReadings? GetReadings(string patientId);

    /// <summary>Merges new readings into the patient's snapshot and returns the merged result.</summary>
    VitalReadings MergeReadings(string patientId, VitalReadings update);
}

public sealed class PatientRegistry : IPatientRegistry
{
    private readonly ConcurrentDictionary<string, PatientInfoDto> _patients = new();
    private readonly ConcurrentDictionary<string, VitalReadings> _readings = new();

    public int Count => _patients.Count;

    public IReadOnlyList<PatientInfoDto> GetAll() =>
        _patients.Values.OrderBy(p => p.BedNumber).ThenBy(p => p.PatientId, StringComparer.Ordinal).ToList();

    public PatientInfoDto? Get(string patientId) =>
        _patients.TryGetValue(patientId, out var patient) ? patient : null;

    public bool Upsert(PatientInfoDto patient)
    {
        var changed = true;
        _patients.AddOrUpdate(
            patient.PatientId,
            _ => patient,
            (_, existing) =>
            {
                changed = existing != patient;
                return patient;
            });
        return changed;
    }

    public bool Remove(string patientId)
    {
        _readings.TryRemove(patientId, out _);
        return _patients.TryRemove(patientId, out _);
    }

    public VitalReadings? GetReadings(string patientId) =>
        _readings.TryGetValue(patientId, out var readings) ? readings : null;

    public VitalReadings MergeReadings(string patientId, VitalReadings update) =>
        _readings.AddOrUpdate(patientId, _ => update, (_, existing) => existing.Merge(update));
}
