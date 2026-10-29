namespace Monitoring.BL.Services;

using System;
using Monitoring.Shared.DTOs;

/// <summary>
/// Synthesises monitor traces from a continuous cycle phase.
///
/// Each waveform is defined analytically as a function of a phase in [0,1) covering
/// exactly one cycle (one heartbeat, one breath). Advancing that phase at a rate
/// derived from the patient's *measured* rate means the trace on screen always matches
/// the numeric beside it — a fixed-length sample table cannot do that, since it locks
/// the trace to one rate no matter what the numeric reads.
/// </summary>
public static class WaveformSynthesizer
{
    /// <summary>Sampling rate for ECG traces, in Hz.</summary>
    public const int EcgSampleRate = 250;

    /// <summary>Sampling rate for pleth and respiration traces, in Hz.</summary>
    public const int SlowSampleRate = 50;

    /// <summary>
    /// Fills <paramref name="destination"/> with the next block of samples and returns
    /// the phase to carry into the following block, so the trace stays continuous.
    /// </summary>
    /// <param name="destination">Buffer to fill.</param>
    /// <param name="type">Which trace to synthesise.</param>
    /// <param name="phase">Cycle phase to start from, in [0,1).</param>
    /// <param name="ratePerMinute">Beats or breaths per minute driving the trace.</param>
    /// <param name="sampleRate">Samples per second represented by the buffer.</param>
    public static double Fill(float[] destination, WaveformType type, double phase, double ratePerMinute, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));

        // Guard against a zero/negative rate producing a frozen or reversed trace.
        var cyclesPerSecond = Math.Max(ratePerMinute, 1.0) / 60.0;
        var increment = cyclesPerSecond / sampleRate;

        for (var i = 0; i < destination.Length; i++)
        {
            destination[i] = SampleAt(type, phase);
            phase += increment;
            if (phase >= 1.0) phase -= Math.Floor(phase);
        }

        return phase;
    }

    private static float SampleAt(WaveformType type, double phase) => type switch
    {
        WaveformType.ECG_II or WaveformType.ECG_I => Ecg(phase),
        WaveformType.Pleth => Pleth(phase),
        WaveformType.Respiration => Respiration(phase),
        _ => 0f
    };

    /// <summary>
    /// PQRST complex built from Gaussian components (the standard McSharry-style model).
    /// Gaussians wrap smoothly across the cycle boundary, which is what keeps the
    /// baseline free of the step artefacts a piecewise-sine table produces.
    /// </summary>
    private static float Ecg(double phase)
    {
        double v = 0;
        v += Gaussian(phase, centre: 0.170, width: 0.0250, amplitude: 0.10);  // P wave
        v += Gaussian(phase, centre: 0.255, width: 0.0075, amplitude: -0.12); // Q
        v += Gaussian(phase, centre: 0.280, width: 0.0085, amplitude: 1.00);  // R
        v += Gaussian(phase, centre: 0.310, width: 0.0100, amplitude: -0.28); // S
        v += Gaussian(phase, centre: 0.460, width: 0.0420, amplitude: 0.28);  // T wave
        return (float)v;
    }

    /// <summary>Photoplethysmogram: systolic upstroke followed by the dicrotic wave.</summary>
    private static float Pleth(double phase)
    {
        double v = Gaussian(phase, centre: 0.160, width: 0.055, amplitude: 1.00)
                 + Gaussian(phase, centre: 0.380, width: 0.075, amplitude: 0.42);
        // Scaled and offset so the trace is centred on the display rather than
        // hugging the bottom of its lane.
        return (float)(v * 0.85 - 0.35);
    }

    /// <summary>Impedance respiration: a smooth breathing cycle.</summary>
    private static float Respiration(double phase) => (float)(0.45 * Math.Sin(2.0 * Math.PI * phase));

    /// <summary>
    /// Gaussian bump evaluated on a circular phase axis, so a component centred near
    /// 0 or 1 bleeds across the wrap point instead of being cut off.
    /// </summary>
    private static double Gaussian(double phase, double centre, double width, double amplitude)
    {
        var d = phase - centre;
        if (d > 0.5) d -= 1.0;
        else if (d < -0.5) d += 1.0;

        return amplitude * Math.Exp(-(d * d) / (2.0 * width * width));
    }
}
