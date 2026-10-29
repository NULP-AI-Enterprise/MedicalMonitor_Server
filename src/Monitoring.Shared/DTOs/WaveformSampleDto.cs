namespace Monitoring.Shared.DTOs;

/// <summary>
/// A batch of waveform samples for rendering on the monitor display.
/// Samples are normalized floating-point values representing the waveform amplitude.
/// </summary>
/// <param name="Type">The type of waveform (ECG_II, ECG_I, Pleth, Respiration).</param>
/// <param name="Samples">Array of normalized sample values (typically -1.0 to 1.0 for ECG, 0.0 to 1.0 for Pleth/Resp).</param>
/// <param name="SampleRate">Sampling rate in Hz (e.g., 250 for ECG, 50 for Pleth).</param>
public record WaveformSampleDto(
    WaveformType Type,
    float[] Samples,
    int SampleRate
);
