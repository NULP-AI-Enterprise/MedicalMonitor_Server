using System.Text;
using System.Text.RegularExpressions;
using MedicalMonitorServer.Contracts.Models;
using MedicalMonitorServer.DeviceDriver.Umec;

namespace MedicalMonitorServer.DeviceDriver.Tests;

/// <summary>
/// Messages captured from a real uMec10 on 2026-07-07 (hl7_*.log format:
/// a "----- hh:mm:ss -----" line before each message). One sample per
/// message id; patient demographics in PID replaced with test values.
/// </summary>
public class RealDumpTests
{
    private static readonly ParserService Parser = new();

    private static readonly IReadOnlyList<string> Dump = Load("hl7_sample.log");

    private static IReadOnlyList<string> Load(string name)
    {
        var text = Encoding.Latin1.GetString(
            File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)));
        return Regex.Split(text, @"^----- [\d:]+ -----\n", RegexOptions.Multiline)
            .Where(b => b.Contains("MSH|"))
            .ToList();
    }

    private static UmecMessage One(int id, Func<UmecMessage, bool>? where = null) =>
        Dump.Select(b => Parser.Parse(b)!)
            .Single(m => m.MessageId == id && (where?.Invoke(m) ?? true));

    [Fact]
    public void EveryCapturedMessage_Parses_WithKnownKind()
    {
        Assert.True(Dump.Count >= 25, $"fixture has {Dump.Count} messages");
        foreach (var block in Dump)
        {
            var m = Parser.Parse(block);
            Assert.NotNull(m);
            Assert.Equal("ORU^R01", m!.MessageType);
            Assert.NotEqual(UmecMessageKind.Unknown, m.Kind);
            Assert.NotNull(m.Oru);
        }
    }

    [Fact]
    public void Nibp_Valid()
    {
        var m = One(503, x => x.Oru!.Vitals.All(v => v.Value != null));
        var byId = m.Oru!.Vitals.ToDictionary(v => v.ParameterId, v => v.Value);
        Assert.Equal(120, byId[170]);
        Assert.Equal(80, byId[171]);
        Assert.Equal(93, byId[172]);
        Assert.All(m.Oru.Vitals, v => Assert.True(v.IsAperiodic));
    }

    [Fact]
    public void Nibp_NoMeasurement_IsNull()
    {
        var m = One(503, x => x.Oru!.Vitals.All(v => v.Value == null));
        Assert.Equal(3, m.Oru!.Vitals.Count);
        Assert.All(m.Oru.Vitals, v => Assert.Equal("-100", v.RawValue));
    }

    [Fact]
    public void AlarmLimits_NegativeLimitsArePreserved()
    {
        var s = Assert.Single(One(51).Oru!.AlarmSettings);
        Assert.Equal(106, s.ParameterId);
        Assert.Equal("ST-II", s.ParameterName);
        Assert.Equal(0.20, s.HighLimit);
        Assert.Equal(-0.20, s.LowLimit);
    }

    [Fact]
    public void AlarmLevels_0High_1Medium_2Low()
    {
        var s = One(58).Oru!.AlarmSettings.ToDictionary(x => x.ParameterId);
        Assert.Equal(1, s[101].LevelCode);
        Assert.Equal(AlarmLevel.Medium, s[101].Level);
        Assert.Equal(AlarmLevel.Low, s[102].Level);
    }

    [Fact]
    public void AlarmSwitch()
    {
        var s = Assert.Single(One(60).Oru!.AlarmSettings);
        Assert.Equal(106, s.ParameterId);
        Assert.False(s.Enabled);
    }

    [Fact]
    public void PhysiologicalAlarm_HrTooLow()
    {
        var a = Assert.Single(One(54, x => x.Oru!.Alarms.Count > 0).Oru!.Alarms);
        Assert.Equal(AlarmCategory.Physiological, a.Category);
        Assert.Equal(10002, a.AlarmId);
        Assert.Equal(AlarmLevel.High, a.Level);
        Assert.StartsWith("ЧСС", a.Text);
        Assert.NotNull(a.RaisedAt);
    }

    [Fact]
    public void NoActivePhysiologicalAlarms()
    {
        var m = One(54, x => x.Oru!.Alarms.Count == 0);
        Assert.Equal(UmecMessageKind.PhysiologicalAlarms, m.Kind);
    }

    [Fact]
    public void TechnicalAlarms()
    {
        var a = One(56).Oru!.Alarms;
        Assert.Equal(2, a.Count);
        Assert.All(a, x => Assert.Equal(AlarmCategory.Technical, x.Category));
        Assert.Equal(new[] { 458, 5 }, a.Select(x => x.AlarmId));
    }

    [Fact]
    public void ParameterLabels_Cyrillic()
    {
        var oru = One(11).Oru!;
        Assert.Equal("ЧСС", oru.ParameterLabels.Single(l => l.ParameterId == 101).Label);
        Assert.Equal("ЭКГ", oru.ParameterGroups.Single(g => g.GroupId == 2101).Label);
    }

    [Fact]
    public void PatientInfo()
    {
        var p = One(103).Oru!.Patient!;
        Assert.Equal("TEST-0001", p.PatientId);
        Assert.Equal(170.0, p.HeightCm);
        Assert.Equal(70.0, p.WeightKg);
        Assert.Equal("812", p.BedNumber);
        Assert.Equal("192.168.0.100", p.MonitorIp);
        Assert.Equal(4601, p.MonitorPort);
        Assert.Equal(PatientType.Adult, p.PatientType);
    }

    [Fact]
    public void AlarmSystemStatus()
    {
        var s = One(53).Oru!.AlarmSystem!;
        Assert.Equal(1, s.Volume);
        Assert.False(s.Muted);
        Assert.False(s.AlarmPaused);
    }

    [Fact]
    public void WaveChannel_EcgII()
    {
        var w = Assert.Single(One(161, x => x.Oru!.WaveChannels.Any(c => c.ChannelId == 1101)).Oru!.WaveChannels);
        Assert.Equal("II", w.Label);
        Assert.Equal(0.019531, w.Scale);
        Assert.Equal("mv", w.ScaleUnit);
        Assert.Equal(1.0, w.Gain);
        Assert.Equal(128, w.Baseline);
        Assert.Equal(256, w.Levels);
        Assert.Equal(0, w.MinSample);
        Assert.Equal(255, w.MaxSample);
    }

    [Fact]
    public void WaveChannel_Respiration()
    {
        var w = Assert.Single(One(161, x => x.Oru!.WaveChannels.Any(c => c.ChannelId == 1151)).Oru!.WaveChannels);
        Assert.Equal("Дых.", w.Label);
        Assert.Equal(1.0, w.Scale);
        Assert.Equal("", w.ScaleUnit);
        Assert.Equal(128, w.Baseline);
        Assert.Equal(128, w.Levels);
    }

    [Fact]
    public void WaveChannel_Pleth()
    {
        var w = Assert.Single(One(161, x => x.Oru!.WaveChannels.Any(c => c.ChannelId == 1152)).Oru!.WaveChannels);
        Assert.Equal("Плет.", w.Label);
        Assert.Equal(2.0, w.Scale);
        Assert.Equal(0, w.Baseline);
        Assert.Equal(127, w.MaxSample);
    }
}
