using MedicalMonitorServer.Contracts.Api;
using MedicalMonitorServer.Contracts.Models;
using MedicalMonitorServer.Core.Monitoring;
using MedicalMonitorServer.DeviceDriver;
using MedicalMonitorServer.DeviceDriver.NihonKohden;
using MedicalMonitorServer.DeviceDriver.Umec;
using MedicalMonitorServer.Services;
using Microsoft.Extensions.Logging;

namespace MedicalMonitorServer.Tests;

/// <summary>
/// A station asks for a patient's cuff measurement to be started or stopped. The server finds
/// the monitor the patient is on, hands the request to the driver that reads it, and writes
/// down what was asked and what became of it.
/// </summary>
public class NibpControlTests
{
    private const string Monitor = "10.10.46.48";

    /// <summary>A driver that can have one monitor's cuff started, and answers as it is told to.</summary>
    private sealed class CuffDriver : IDeviceDataProvider, IRemoteCuff
    {
        public CuffRequestStatus Answer { get; set; } = CuffRequestStatus.Accepted;

        public List<(string MonitorIp, bool Start)> Requests { get; } = new();

#pragma warning disable CS0067 // link state and messages are not what these tests are about
        public event Action<IDeviceDataProvider, DeviceState>? OnDeviceConnected;

        public event Action<IDeviceDataProvider, DeviceState>? OnDeviceDisconnected;

        public event Action<UmecMessage>? OnMessageReceived;
#pragma warning restore CS0067

        public IReadOnlyCollection<DeviceState> ConnectedDevices => Array.Empty<DeviceState>();

        public IReadOnlyCollection<PatientInfo> Patients => Array.Empty<PatientInfo>();

        public void Connect(DeviceState deviceState) { }

        public void Disconnect(DeviceState deviceState) { }

        public bool CanControlCuff(string monitorIp) => monitorIp == Monitor;

        public CuffRequestStatus RequestCuff(string monitorIp, bool start)
        {
            if (monitorIp != Monitor) return CuffRequestStatus.NotSupported;

            Requests.Add((monitorIp, start));
            return Answer;
        }

        public void Dispose() { }
    }

    private readonly CuffDriver _driver = new();
    private readonly PatientRegistry _patients = new();
    private readonly MonitorBindings _bindings = new();
    private readonly CapturingLogger<NibpControl> _log = new();
    private readonly DeviceService _devices;
    private readonly NibpControl _nibp;

    public NibpControlTests()
    {
        // The plain provider stands for the uMec driver: a monitor it reads has no cuff to command.
        _devices = new DeviceService(
            new FakeWatchDog(), loggerFactory: null, providers: new IDeviceDataProvider[] { new FakeProvider(), _driver });
        _nibp = new NibpControl(_patients, _bindings, _devices, _log);

        Admit("P1", Monitor);
    }

    /// <summary>Puts a patient on the roster and, when an address is given, on that monitor.</summary>
    private void Admit(string patientId, string? monitorIp)
    {
        _patients.Upsert(new PatientInfoDto(patientId, "Ivan Petrenko", 1, null));
        if (monitorIp is not null) _bindings.Report(monitorIp, patientId, 1, hasReadings: true, DateTimeOffset.UtcNow);
    }

    [Fact]
    public void A_start_goes_to_the_monitor_the_patient_is_on()
    {
        var result = _nibp.Request("P1", start: true, requestedFrom: "127.0.0.1");

        Assert.Equal(new NibpRequestResult(NibpRequestOutcome.Accepted, Monitor), result);
        Assert.Null(result.Refusal);
        Assert.Equal(new[] { (Monitor, true) }, _driver.Requests);
    }

    [Fact]
    public void A_stop_goes_the_same_way()
    {
        var result = _nibp.Request("P1", start: false);

        Assert.Equal(NibpRequestOutcome.Accepted, result.Outcome);
        Assert.Equal(new[] { (Monitor, false) }, _driver.Requests);
    }

    [Fact]
    public void Every_request_is_written_down_with_its_outcome_and_where_it_came_from()
    {
        _nibp.Request("P1", start: true, requestedFrom: "192.168.1.20");

        var entry = Assert.Single(_log.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal($"NIBP start for patient P1 on monitor {Monitor}, asked from 192.168.1.20: Accepted.", entry.Message);
    }

    [Fact]
    public void A_patient_nobody_knows_is_not_found_and_no_monitor_is_told()
    {
        var result = _nibp.Request("NOBODY", start: true);

        Assert.Equal(NibpRequestOutcome.PatientNotFound, result.Outcome);
        Assert.Empty(_driver.Requests);
    }

    [Fact]
    public void A_patient_on_no_monitor_has_no_cuff_to_start()
    {
        Admit("P2", monitorIp: null);

        var result = _nibp.Request("P2", start: true);

        Assert.Equal(new NibpRequestResult(NibpRequestOutcome.NoMonitor, null), result);
        Assert.Equal("The patient is not on a monitor.", result.Refusal);
        Assert.Empty(_driver.Requests);
    }

    [Fact]
    public void A_monitor_no_driver_can_command_is_said_to_be_one()
    {
        // A uMec: read by a driver that has no cuff command.
        Admit("P3", "192.168.2.101");

        var result = _nibp.Request("P3", start: true);

        Assert.Equal(new NibpRequestResult(NibpRequestOutcome.NotSupported, "192.168.2.101"), result);
        Assert.False(_devices.CanControlCuff("192.168.2.101"));
        Assert.True(_devices.CanControlCuff(Monitor));
    }

    [Theory]
    [InlineData(CuffRequestStatus.NotAllowed, NibpRequestOutcome.NotAllowed, "turned off on this server")]
    [InlineData(CuffRequestStatus.NotStreaming, NibpRequestOutcome.NotStreaming, "not sending data")]
    [InlineData(CuffRequestStatus.AlreadyMeasuring, NibpRequestOutcome.AlreadyMeasuring, "already running")]
    [InlineData(CuffRequestStatus.TooSoon, NibpRequestOutcome.TooSoon, "a moment ago")]
    [InlineData(CuffRequestStatus.NotSupported, NibpRequestOutcome.NotSupported, "cannot be told")]
    public void What_the_driver_refuses_the_station_is_told_in_words(
        CuffRequestStatus fromDriver, NibpRequestOutcome expected, string words)
    {
        _driver.Answer = fromDriver;

        var result = _nibp.Request("P1", start: true);

        Assert.Equal(expected, result.Outcome);
        Assert.Contains(words, result.Refusal);

        // A refusal is worth a warning: somebody wanted a cuff inflated and it was not.
        Assert.Equal(LogLevel.Warning, Assert.Single(_log.Entries).Level);
    }

    [Fact]
    public void A_real_LsNet_driver_refuses_until_the_option_is_turned_on()
    {
        // LS-NET with no local address: the driver is built and stays off the network.
        static DeviceService Service(bool remoteNibp) =>
            new(new FakeWatchDog(), loggerFactory: null, providers: new[] { new FakeProvider() },
                nihonKohden: new NihonKohdenOptions
                {
                    Monitors = { new NihonKohdenMonitor { Name = "BSM-3562", Ip = Monitor, Protocol = "LsNet" } },
                    LsNet = { RemoteNibp = remoteNibp },
                });

        Assert.False(Service(remoteNibp: false).CanControlCuff(Monitor));
        Assert.Equal(CuffRequestStatus.NotAllowed, Service(remoteNibp: false).RequestCuff(Monitor, start: true));

        // Allowed, and the monitor is not streaming: nothing to send the command through.
        Assert.Equal(CuffRequestStatus.NotStreaming, Service(remoteNibp: true).RequestCuff(Monitor, start: true));
        Assert.Equal(CuffRequestStatus.NotSupported, Service(remoteNibp: true).RequestCuff("192.168.2.101", start: true));
    }
}
