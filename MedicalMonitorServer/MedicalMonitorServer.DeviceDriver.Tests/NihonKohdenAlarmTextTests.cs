using MedicalMonitorServer.DeviceDriver.NihonKohden;

namespace MedicalMonitorServer.DeviceDriver.Tests;

/// <summary>
/// A Nihon Kohden sends its technical alarms as numbers - a parameter code and a message
/// code - and the station needs words a clinician can act on: which sensor, and what about it.
/// </summary>
public class NihonKohdenAlarmTextTests
{
    [Theory]
    [InlineData("044000", "ART")]
    [InlineData("044001", "ART")]
    [InlineData("044002", "ART")]
    [InlineData("053002", "CVP")]
    [InlineData("009001", "NIBP")]
    [InlineData("007000", "SpO2")]
    [InlineData("003007", "ECG")]
    [InlineData("005000", "Respiration")]
    [InlineData("027000", "Temperature 1")]
    [InlineData("028000", "Temperature 2")]
    public void Every_parameter_of_a_sensor_names_that_sensor(string code, string sensor)
    {
        Assert.Equal(sensor, NihonKohdenCodes.Sensor(code));
    }

    [Fact]
    public void A_parameter_of_no_known_sensor_is_named_by_its_code()
    {
        Assert.Equal("Parameter 037000", NihonKohdenCodes.Sensor("037000"));
    }

    [Fact]
    public void Message_04_is_a_connector_that_is_off_whatever_the_sensor()
    {
        Assert.Equal("NIBP connector off", NihonKohdenCodes.TechnicalAlarmText("NIBP", 0x04));
        Assert.Equal("ART connector off", NihonKohdenCodes.TechnicalAlarmText("ART", 0x04));
        Assert.Equal("SpO2 connector off", NihonKohdenCodes.TechnicalAlarmText("SpO2", 0x04));
    }

    [Fact]
    public void Message_13_on_the_cuff_is_a_measurement_that_failed()
    {
        Assert.Equal("NIBP measurement failed", NihonKohdenCodes.TechnicalAlarmText("NIBP", 0x13));
    }

    [Fact]
    public void A_message_nobody_has_named_says_to_check_the_sensor_and_keeps_its_number()
    {
        Assert.Equal("Temperature 1: check sensor (05)", NihonKohdenCodes.TechnicalAlarmText("Temperature 1", 0x05));
        Assert.Equal("ART: check sensor (13)", NihonKohdenCodes.TechnicalAlarmText("ART", 0x13));
    }
}
