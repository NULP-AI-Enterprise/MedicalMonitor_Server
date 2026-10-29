namespace Monitoring.BL.Services;

using System;

/// <summary>
/// Cadence of the simulation. The per-update sample counts are derived from the tick
/// period rather than hard-coded, so the traces sweep at real time regardless of how
/// the interval is tuned.
/// </summary>
public static class GeneratorSettings
{
    /// <summary>How often a monitor update is generated and broadcast.</summary>
    public const int UpdateIntervalMs = 500;

    public static readonly TimeSpan UpdateInterval = TimeSpan.FromMilliseconds(UpdateIntervalMs);

    /// <summary>ECG samples carried by one update (250 Hz × 0.5 s = 125).</summary>
    public const int EcgSamplesPerUpdate = WaveformSynthesizer.EcgSampleRate * UpdateIntervalMs / 1000;

    /// <summary>Pleth/respiration samples carried by one update (50 Hz × 0.5 s = 25).</summary>
    public const int SlowSamplesPerUpdate = WaveformSynthesizer.SlowSampleRate * UpdateIntervalMs / 1000;
}
