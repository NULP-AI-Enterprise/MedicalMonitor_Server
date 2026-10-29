namespace Monitoring.Shared.DTOs;

/// <summary>
/// Complete monitor update pushed via SignalR for a single patient.
/// Contains both numeric vitals and waveform trace data.
/// </summary>
/// <param name="PatientId">Unique patient identifier.</param>
/// <param name="PatientName">Display name for the patient.</param>
/// <param name="BedNumber">Bed/station number in the unit.</param>
/// <param name="Vitals">Current numeric vital sign readings.</param>
/// <param name="Waveforms">Array of waveform sample batches for rendering traces.</param>
/// <param name="Timestamp">Server timestamp when this update was generated.</param>
public record MonitorUpdateDto(
    string PatientId,
    string PatientName,
    int BedNumber,
    VitalsDto Vitals,
    WaveformSampleDto[] Waveforms,
    DateTimeOffset Timestamp
);
