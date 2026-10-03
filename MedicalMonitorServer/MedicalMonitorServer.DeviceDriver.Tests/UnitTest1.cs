using MedicalMonitorServer.Contracts.Models;
using MedicalMonitorServer.DeviceDriver.Umec;

namespace MedicalMonitorServer.DeviceDriver.Tests;

public class ParserServiceTests
{
    private readonly ParserService _parserService = new();

    [Fact]
    public void Parse_ReturnsAdtSpecificModel_ForAdtA01()
    {
        const string payload =
            "MSH|^~\\&|MONITOR-01|uMEC-Model|HIS|HOSP|20261003120000||ADT^A01|CTRL-ADT-1|P|2.5\r" +
            "EVN|A01|20261003115900\r" +
            "PID|1|12345|12345^^^HOSP^MR||Doe^John||19800101|M\r" +
            "PV1|1|I|WARD1^101^1\r";

        var message = _parserService.Parse(payload);

        var adtMessage = Assert.IsType<UmecAdtMessage>(message);
        Assert.NotNull(adtMessage.Monitor);
        Assert.Equal("MONITOR-01", adtMessage.Monitor!.DeviceId);
        Assert.Equal("A01", adtMessage.EventCode);
        Assert.Equal("CTRL-ADT-1", adtMessage.ControlId);
        Assert.False(message is UmecOruMessage);
    }

    [Fact]
    public void Parse_ReturnsOruSpecificModel_ForOruR01()
    {
        const string payload =
            "MSH|^~\\&|MONITOR-01|uMEC-Model|HIS|HOSP|20261003120000||ORU^R01|CTRL-ORU-1|P|2.5\r" +
            "PID|1|12345|12345^^^HOSP^MR||Doe^Jane||19850503|F\r" +
            "OBR|1|||Vitals\r" +
            "OBX|1|NM|HR^Heart Rate^VITALS||75|bpm|||||F\r" +
            "OBX|2|NM|SPO2^SpO2^VITALS||98|%|||||F\r";

        var message = _parserService.Parse(payload);

        var oruMessage = Assert.IsType<UmecOruMessage>(message);
        Assert.Equal("CTRL-ORU-1", oruMessage.ControlId);
        Assert.Equal(2, oruMessage.Observations.Count);
        Assert.Equal(2, oruMessage.VitalSigns.Count);
        Assert.Single(oruMessage.ParameterGroups);
        Assert.Equal("VITALS", oruMessage.ParameterGroups[0].Name);
        Assert.False(message is UmecAdtMessage);
    }
}
