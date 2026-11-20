using MedicalMonitorServer.Contracts.Api;
using MedicalMonitorServer.Contracts.Models;

namespace MedicalMonitorServer.Core.Monitoring;

/// <summary>
/// Turns what a uMec says about a waveform channel (its descriptor, OBX 2153) and the raw
/// blocks of samples it sends for it (msg 157) into the wire form a station draws.
/// </summary>
public static class UmecWaveforms
{
    /// <summary>Wire type of a monitor channel; null for a channel stations have no trace for.</summary>
    public static WaveformType? TypeOf(WaveChannel channel) => channel.ChannelId switch
    {
        1151 => WaveformType.Respiration,
        1152 => WaveformType.Pleth,

        // An ECG channel carries whichever lead is selected for it on the monitor; only its
        // label says which. A lead without a wire type is not passed off as another one.
        >= 1100 and < 1150 => channel.Label switch
        {
            "II" => WaveformType.ECG_II,
            "I" => WaveformType.ECG_I,
            _ => null,
        },
        _ => null,
    };

    public static WaveformChannelDto ToChannelDto(WaveChannel channel) => new(
        channel.ChannelId,
        TypeOf(channel),
        channel.Label,
        channel.Scale,
        channel.ScaleUnit,
        channel.Baseline,
        channel.SampleRate,
        channel.MinSample,
        channel.MaxSample);

    /// <summary>
    /// Scales one block of raw samples to the values stations draw (see
    /// <see cref="WaveformSampleDto"/>): a channel measured in millivolts — an ECG — becomes
    /// millivolts around its zero line, any other channel is centred on the middle of its
    /// range and divided by the range, which puts it within -0.5 .. 0.5.
    /// Returns null when the channel has no wire type or its descriptor lacks what the
    /// scaling needs; a trace is never drawn from guessed numbers.
    /// </summary>
    public static WaveformSampleDto? ToSampleDto(WaveChannel channel, WaveSamples block)
    {
        if (block.Samples.Length == 0 || TypeOf(channel) is not { } type) return null;

        double zero;
        double step;
        if (channel is { Scale: > 0, Baseline: { } baseline } &&
            string.Equals(channel.ScaleUnit, "mv", StringComparison.OrdinalIgnoreCase))
        {
            zero = baseline;
            step = channel.Scale.Value;
        }
        else if (channel is { MinSample: { } min, MaxSample: { } max } && max > min)
        {
            zero = (min + max) / 2.0;
            step = 1.0 / (max - min);
        }
        else
        {
            return null;
        }

        var samples = new float[block.Samples.Length];
        for (int i = 0; i < samples.Length; i++)
        {
            // Four decimals is finer than one step of an 8-bit sample on either scale, and
            // keeps a second of ECG on the wire as "-1.1719", not "-1.1718601".
            samples[i] = (float)Math.Round((block.Samples[i] - zero) * step, 4);
        }

        // A block is one second of the channel, so its length is the rate when the monitor did not state one.
        return new WaveformSampleDto(type, samples, channel.SampleRate is > 0 and var rate ? rate : samples.Length);
    }
}
