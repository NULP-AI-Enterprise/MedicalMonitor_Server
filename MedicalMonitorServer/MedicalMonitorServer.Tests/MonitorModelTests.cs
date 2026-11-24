using MedicalMonitorServer.DeviceDriver;
using MedicalMonitorServer.DeviceDriver.NihonKohden;

namespace MedicalMonitorServer.Tests;

/// <summary>
/// A station shows which device a bed is on. The server knows it from how it reads the
/// monitor: the Nihon Kohden monitors are named in configuration, everything else came in
/// through the uMec driver.
/// </summary>
public class MonitorModelTests
{
    private static DeviceService Service(params (string Name, string Ip)[] nihonKohden) =>
        new(new FakeWatchDog(), loggerFactory: null, providers: new[] { new FakeProvider() },
            nihonKohden: new NihonKohdenOptions
            {
                // LS-NET with no local address: the driver is built and stays off the network.
                Monitors = nihonKohden
                    .Select(m => new NihonKohdenMonitor { Name = m.Name, Ip = m.Ip, Protocol = "LsNet" })
                    .ToList(),
            });

    [Fact]
    public void A_configured_Nihon_Kohden_carries_its_make_and_the_name_it_was_given()
    {
        var service = Service(("BSM-3562", "10.10.46.48"));

        Assert.Equal("Nihon Kohden BSM-3562", service.DescribeMonitor("10.10.46.48"));
    }

    [Fact]
    public void The_make_is_not_said_twice_and_is_all_there_is_without_a_name()
    {
        var service = Service(("Nihon Kohden BSM-6701", "10.10.46.48"), ("", "10.10.46.49"));

        Assert.Equal("Nihon Kohden BSM-6701", service.DescribeMonitor("10.10.46.48"));
        Assert.Equal("Nihon Kohden", service.DescribeMonitor("10.10.46.49"));
    }

    [Fact]
    public void Any_other_monitor_is_one_the_uMec_driver_reads()
    {
        var service = Service(("BSM-3562", "10.10.46.48"));

        Assert.Equal("Mindray uMEC", service.DescribeMonitor("192.168.2.101"));
    }
}
