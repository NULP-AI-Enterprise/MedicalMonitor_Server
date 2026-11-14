using System.Text;
using MedicalMonitorServer.Contracts.Models;
using MedicalMonitorServer.DeviceDriver.Umec;

namespace MedicalMonitorServer.DeviceDriver.Tests;

/// <summary>
/// Messages are written in the shape the uMec10 sends them (see the parser's
/// docs). They are hand-assembled samples: swap in captured hl7_*.log dumps
/// via <see cref="Msg"/> when real ones are available.
/// </summary>
public class ParserServiceTests
{
    private readonly ParserService _parser = new();

    private static string Msg(int id, params string[] obx) =>
        "\u000b" + string.Join("\r", new[]
        {
            $"MSH|^~\\&|||||20260707191824||ORU^R01|{id}|P|2.3.1",
            "PID|1||12345||Ivanov^Ivan||19800101|M",
        }.Concat(obx)) + "\r\u001c\r";

    /// <summary>Cyrillic as the monitor sends it: Windows-1251 bytes shifted by -0x10, one char per byte.</summary>
    private static string Wire(string cyrillic)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var bytes = Encoding.GetEncoding(1251).GetBytes(cyrillic);
        for (int i = 0; i < bytes.Length; i++)
            if (bytes[i] >= 0x90) bytes[i] -= 0x10;
        return Encoding.Latin1.GetString(bytes);
    }

    [Fact]
    public void NotHl7_ReturnsNull()
    {
        Assert.Null(_parser.Parse("hello world"));
        Assert.Null(_parser.Parse(Array.Empty<byte>()));
    }

    [Fact]
    public void MllpFraming_IsIgnored_AndBytesAndStringAgree()
    {
        var text = Msg(503, "OBX|1|NM|170^Sys||120|mmHg|||||F|||20260707191800");
        var fromString = _parser.Parse(text)!;
        var fromBytes = _parser.Parse(Encoding.Latin1.GetBytes(text))!;

        Assert.Equal(UmecMessageKind.Vitals, fromString.Kind);
        Assert.Equal(503, fromString.MessageId);
        Assert.Equal(fromString.Raw, fromBytes.Raw);
        Assert.DoesNotContain('\u000b', fromString.Raw);
        Assert.DoesNotContain('\u001c', fromString.Raw);
    }

    [Theory]
    [InlineData("-100")]
    [InlineData("-100.0")]
    [InlineData("-100.00")]
    [InlineData("-1000.0")]
    [InlineData("-10000.000")]
    [InlineData("-1e2")]
    [InlineData("")]
    [InlineData("abc")]
    public void InvalidSentinels_BecomeNullValue_ButRawIsKept(string raw)
    {
        var m = _parser.Parse(Msg(503, $"OBX|1|NM|170^Sys||{raw}|mmHg|||||F"))!;

        var v = Assert.Single(m.Oru!.Vitals);
        Assert.Null(v.Value);
        Assert.Equal(raw, v.RawValue);
    }

    [Theory]
    [InlineData("-99.9", -99.9)]
    [InlineData("0", 0)]
    [InlineData("72.0", 72.0)]
    [InlineData("36.6", 36.6)]
    public void ValidValues_AreParsed(string raw, double expected)
    {
        var m = _parser.Parse(Msg(503, $"OBX|1|NM|101^HR||{raw}|bpm|||||F"))!;

        var v = Assert.Single(m.Oru!.Vitals);
        Assert.Equal(expected, v.Value);
        Assert.Equal(101, v.ParameterId);
        Assert.Equal("Heart Rate (HR)", v.Name);
    }

    [Fact]
    public void Nibp_AperiodicWithTimestamp()
    {
        var m = _parser.Parse(Msg(503,
            "OBX|1|NM|170^Sys|2105|120|mmHg|||||F||APERIODIC|20260707191800",
            "OBX|2|NM|171^Dia|2105|80|mmHg|||||F||APERIODIC|20260707191800",
            "OBX|3|NM|172^Mean|2105|93|mmHg|||||F||APERIODIC|20260707191800"))!;

        Assert.Equal(3, m.Oru!.Vitals.Count);
        Assert.All(m.Oru.Vitals, v =>
        {
            Assert.True(v.IsAperiodic);
            Assert.Equal(2105, v.ModuleId);
            Assert.Equal(new DateTime(2026, 7, 7, 19, 18, 0), v.MeasuredAt);
        });
        Assert.Equal(new double?[] { 120, 80, 93 }, m.Oru.Vitals.Select(v => v.Value));
    }

    [Fact]
    public void AlarmLimits_Msg51()
    {
        var m = _parser.Parse(Msg(51,
            "OBX|1|NM|2002^High limit|101|120|||||F",
            "OBX|2|NM|2003^Low limit|101|50|||||F",
            "OBX|3|NM|2002^High limit|160|100|||||F",
            "OBX|4|NM|2003^Low limit|160|90.0|||||F"))!;

        Assert.Equal(UmecMessageKind.AlarmLimits, m.Kind);
        var s = m.Oru!.AlarmSettings;
        Assert.Equal(2, s.Count);

        var hr = s.Single(x => x.ParameterId == 101);
        Assert.Equal(120, hr.HighLimit);
        Assert.Equal(50, hr.LowLimit);
        Assert.Equal("bpm", hr.Unit);
        Assert.Equal("Heart Rate (HR)", hr.ParameterName);

        var spo2 = s.Single(x => x.ParameterId == 160);
        Assert.Equal(100, spo2.HighLimit);
        Assert.Equal(90, spo2.LowLimit);
        Assert.Equal("%", spo2.Unit);
    }

    [Theory]
    [InlineData("-100")]
    [InlineData("-100.0")]
    public void AlarmLimit_Sentinel_MeansNoLimit(string raw)
    {
        var m = _parser.Parse(Msg(51,
            $"OBX|1|NM|2002^High limit|200|{raw}|||||F",
            "OBX|2|NM|2003^Low limit|200|35.0|||||F"))!;

        var s = Assert.Single(m.Oru!.AlarmSettings);
        Assert.Null(s.HighLimit);
        Assert.Equal(35.0, s.LowLimit);
    }

    [Fact]
    public void AlarmSwitchesAndLevels_Msg60_Msg58()
    {
        var sw = _parser.Parse(Msg(60,
            "OBX|1|CE|2004^Alarm switch|101|1^On|||||F",
            "OBX|2|CE|2004^Alarm switch|160|0^Off|||||F"))!;
        Assert.Equal(UmecMessageKind.AlarmSwitches, sw.Kind);
        Assert.True(sw.Oru!.AlarmSettings.Single(x => x.ParameterId == 101).Enabled);
        Assert.False(sw.Oru.AlarmSettings.Single(x => x.ParameterId == 160).Enabled);

        var lv = _parser.Parse(Msg(58,
            "OBX|1|CE|2009^Alarm level|101|1^High|||||F",
            "OBX|2|CE|2009^Alarm level|160|2^Med|||||F"))!;
        Assert.Equal(UmecMessageKind.AlarmLevels, lv.Kind);
        Assert.Equal(AlarmLevel.High, lv.Oru!.AlarmSettings.Single(x => x.ParameterId == 101).Level);
        Assert.Equal(AlarmLevel.Medium, lv.Oru.AlarmSettings.Single(x => x.ParameterId == 160).Level);
    }

    [Fact]
    public void ActiveAlarms_LevelFromAsterisks()
    {
        var m = _parser.Parse(Msg(54,
            "OBX|1|CE|1^Phys|1|10002^***HR too low|||||F||||20260707191800",
            "OBX|2|CE|1^Phys|2|10020^**SpO2 low|||||F"))!;

        Assert.Equal(UmecMessageKind.PhysiologicalAlarms, m.Kind);
        var a = m.Oru!.Alarms;
        Assert.Equal(2, a.Count);
        Assert.Equal(AlarmCategory.Physiological, a[0].Category);
        Assert.Equal(AlarmLevel.High, a[0].Level);
        Assert.Equal(10002, a[0].AlarmId);
        Assert.Equal("HR too low", a[0].Text);
        Assert.Equal(AlarmLevel.Medium, a[1].Level);
    }

    [Fact]
    public void EmptyAlarmList_IsTechnicalAlarmsKindByMessageId()
    {
        var m = _parser.Parse(Msg(56))!;
        Assert.Equal(UmecMessageKind.TechnicalAlarms, m.Kind);
        Assert.Empty(m.Oru!.Alarms);
    }

    [Fact]
    public void ParameterLabels_CyrillicIsUnshifted()
    {
        var m = _parser.Parse(Msg(11,
            $"OBX|1|CE|2025^Label|2101|101^{Wire("ЧСС")}|||||F",
            $"OBX|2|CE|2023^Group||2101^{Wire("ЕКГ")}|||||F"))!;

        Assert.Equal(UmecMessageKind.ParameterLabels, m.Kind);
        var l = Assert.Single(m.Oru!.ParameterLabels);
        Assert.Equal(101, l.ParameterId);
        Assert.Equal("ЧСС", l.Label);
        Assert.Equal(2101, l.GroupId);
        Assert.Equal("ЕКГ", Assert.Single(m.Oru.ParameterGroups).Label);
    }

    [Fact]
    public void AdtBeacon_MonitorAndPatient()
    {
        var text = string.Join("\r", new[]
        {
            "MSH|^~\\&|||||20260707191824||ADT^A01|1|P|2.3.1",
            "EVN|A01|20260707",
            "PID|1||12345||Ivanov^Ivan||19800101|M",
            "PV1|1|I|^^ICU&12&3232235620&4601&&1|||||||||||||||A",
            "OBX|1|ST|2304^Monitor name||BED-12|||||F",
            "OBX|2|CE|2305^Standby||0|||||F",
        });
        var m = _parser.Parse(text)!;

        Assert.Equal(UmecMessageKind.MonitorStatus, m.Kind);
        Assert.Null(m.Oru);
        Assert.Equal("BED-12", m.Adt!.Monitor!.Name);
        Assert.False(m.Adt.Monitor.Standby);
        Assert.Equal("Ivanov", m.Adt.Patient!.LastName);
        Assert.Equal("192.168.0.100", m.Adt.Patient.MonitorIp);
        Assert.Equal(4601, m.Adt.Patient.MonitorPort);
        Assert.Equal(PatientType.Adult, m.Adt.Patient.PatientType);
    }
}
