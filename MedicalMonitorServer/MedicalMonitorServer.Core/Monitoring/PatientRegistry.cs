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

    /// <summary>Latest waveform channel descriptors of the patient's monitor; empty when none are known.</summary>
    WaveformChannelDto[] GetWaveformChannels(string patientId);

    /// <summary>Merges <paramref name="channels"/> into the patient's known channels by channel id.</summary>
    void UpdateWaveformChannels(string patientId, IEnumerable<WaveformChannelDto> channels);
}

public sealed class PatientRegistry : IPatientRegistry
{
    private readonly ConcurrentDictionary<string, PatientInfoDto> _patients = new();
    private readonly ConcurrentDictionary<string, VitalReadings> _readings = new();
    private readonly ConcurrentDictionary<string, WaveformChannelDto[]> _channels = new();

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
        _channels.TryRemove(patientId, out _);
        return _patients.TryRemove(patientId, out _);
    }

    public VitalReadings? GetReadings(string patientId) =>
        _readings.TryGetValue(patientId, out var readings) ? readings : null;

    public VitalReadings MergeReadings(string patientId, VitalReadings update) =>
        _readings.AddOrUpdate(patientId, _ => update, (_, existing) => existing.Merge(update));

    public WaveformChannelDto[] GetWaveformChannels(string patientId) =>
        _channels.TryGetValue(patientId, out var channels) ? channels : Array.Empty<WaveformChannelDto>();

    // The monitor announces one channel per message (msg 160/161), so channels are merged, not replaced.
    public void UpdateWaveformChannels(string patientId, IEnumerable<WaveformChannelDto> channels)
    {
        var update = channels.ToList();
        if (update.Count == 0) return;

        _channels.AddOrUpdate(
            patientId,
            _ => update.OrderBy(c => c.ChannelId).ToArray(),
            (_, existing) => existing
                .Where(e => update.All(u => u.ChannelId != e.ChannelId))
                .Concat(update)
                .OrderBy(c => c.ChannelId)
                .ToArray());
    }
}
