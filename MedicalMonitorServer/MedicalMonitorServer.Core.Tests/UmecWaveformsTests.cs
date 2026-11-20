using MedicalMonitorServer.Contracts.Api;
using MedicalMonitorServer.Contracts.Models;
using MedicalMonitorServer.Core.Monitoring;

namespace MedicalMonitorServer.Core.Tests;

/// <summary>Channel descriptors are the ones a uMec10 sends (OBX 2153), see the driver's RealDumpTests.</summary>
public class UmecWaveformsTests
{
    private static WaveChannel Ecg(string label = "II", int id = 1101) => new()
    {
        ChannelId = id, Label = label, Scale = 0.019531, ScaleUnit = "mv", Gain = 1.0,
        Baseline = 128, SampleRate = 256, MinSample = 0, MaxSample = 255,
    };

    private static WaveChannel Respiration() => new()
    {
        ChannelId = 1151, Label = "Resp", Scale = 1.0, ScaleUnit = "", Gain = 1.0,
        Baseline = 128, SampleRate = 128, MinSample = 0, MaxSample = 255,
    };

    private static WaveChannel Pleth() => new()
    {
        ChannelId = 1152, Label = "Pleth", Scale = 2.0, ScaleUnit = "", Gain = 1.0,
        Baseline = 0, SampleRate = 256, MinSample = 0, MaxSample = 127,
    };

    private static WaveSamples Block(int channelId, params byte[] samples) =>
        new() { ChannelId = channelId, Samples = samples };

    [Fact]
    public void Type_FollowsTheChannel_AndForEcgTheLeadOnIt()
    {
        Assert.Equal(WaveformType.Respiration, UmecWaveforms.TypeOf(Respiration()));
        Assert.Equal(WaveformType.Pleth, UmecWaveforms.TypeOf(Pleth()));
        Assert.Equal(WaveformType.ECG_II, UmecWaveforms.TypeOf(Ecg("II")));
        Assert.Equal(WaveformType.ECG_I, UmecWaveforms.TypeOf(Ecg("I", id: 1102)));

        // The same channel with another lead selected on the monitor is that lead, not lead II.
        Assert.Equal(WaveformType.ECG_I, UmecWaveforms.TypeOf(Ecg("I")));
    }

    [Theory]
    [InlineData(1107, "V")]
    [InlineData(1101, "III")]
    [InlineData(1101, "")]
    [InlineData(1301, "II")]
    public void ChannelWithoutAWireType_HasNoType_AndIsNotScaled(int channelId, string label)
    {
        var channel = Ecg(label, channelId);

        Assert.Null(UmecWaveforms.TypeOf(channel));
        Assert.Null(UmecWaveforms.ToSampleDto(channel, Block(channelId, 128, 129)));
    }

    [Fact]
    public void Ecg_IsInMillivoltsAroundItsZeroLine()
    {
        var dto = UmecWaveforms.ToSampleDto(Ecg(), Block(1101, 128, 179, 77, 0, 255))!;

        Assert.Equal(WaveformType.ECG_II, dto.Type);
        Assert.Equal(256, dto.SampleRate);
        // 51 steps of 0.019531 mV are 1 mV; the 8-bit range is -2.5 .. +2.48 mV.
        Assert.Equal(new[] { 0f, 0.9961f, -0.9961f, -2.5f, 2.4804f }, dto.Samples);
    }

    [Fact]
    public void Pleth_IsCentredOnTheMiddleOfItsRange()
    {
        var dto = UmecWaveforms.ToSampleDto(Pleth(), Block(1152, 0, 127, 64))!;

        Assert.Equal(WaveformType.Pleth, dto.Type);
        Assert.Equal(256, dto.SampleRate);
        Assert.Equal(new[] { -0.5f, 0.5f, 0.0039f }, dto.Samples);
    }

    [Fact]
    public void Respiration_IsCentredOnTheMiddleOfItsRange()
    {
        var dto = UmecWaveforms.ToSampleDto(Respiration(), Block(1151, 0, 255, 128))!;

        Assert.Equal(WaveformType.Respiration, dto.Type);
        Assert.Equal(128, dto.SampleRate);
        Assert.Equal(new[] { -0.5f, 0.5f, 0.002f }, dto.Samples);
    }

    [Fact]
    public void EcgWithoutAMillivoltScale_FallsBackToItsRange()
    {
        var channel = Ecg();
        channel.ScaleUnit = "";

        var dto = UmecWaveforms.ToSampleDto(channel, Block(1101, 0, 255))!;

        Assert.Equal(new[] { -0.5f, 0.5f }, dto.Samples);
    }

    [Fact]
    public void SampleRate_IsTheBlockLength_WhenTheMonitorGaveNone()
    {
        var channel = Pleth();
        channel.SampleRate = null;

        var dto = UmecWaveforms.ToSampleDto(channel, Block(1152, new byte[100]))!;

        Assert.Equal(100, dto.SampleRate);
    }

    [Fact]
    public void NothingIsDrawnFromADescriptorThatCannotScale()
    {
        var noRange = Respiration();
        noRange.MinSample = null;

        var emptyRange = Pleth();
        emptyRange.MaxSample = 0;

        Assert.Null(UmecWaveforms.ToSampleDto(noRange, Block(1151, 1, 2, 3)));
        Assert.Null(UmecWaveforms.ToSampleDto(emptyRange, Block(1152, 1, 2, 3)));
        Assert.Null(UmecWaveforms.ToSampleDto(Pleth(), Block(1152)));
    }

    [Fact]
    public void ChannelDto_CarriesTheDescriptor()
    {
        var dto = UmecWaveforms.ToChannelDto(Ecg());

        Assert.Equal(
            new WaveformChannelDto(1101, WaveformType.ECG_II, "II", 0.019531, "mv", 128, 256, 0, 255),
            dto);
        Assert.Null(UmecWaveforms.ToChannelDto(Ecg("V", 1107)).Type);
    }
}
