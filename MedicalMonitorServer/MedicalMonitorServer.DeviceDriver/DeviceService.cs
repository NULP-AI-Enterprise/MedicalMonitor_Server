using MedicalMonitorServer.Contracts.Models;
using MedicalMonitorServer.DeviceDriver.NihonKohden;
using MedicalMonitorServer.DeviceDriver.NihonKohden.LsNet;
using MedicalMonitorServer.DeviceDriver.Umec;
using Microsoft.Extensions.Logging;

namespace MedicalMonitorServer.DeviceDriver;

public class DeviceService : IDeviceService, IMonitorAdmissions
{
    public event Action<UmecMessage>? OnMessageReceived;

    private static readonly Task<MonitorAdmissionResult> NotSupported =
        Task.FromResult(new MonitorAdmissionResult(MonitorAdmissionStatus.NotSupported));

    private readonly NihonKohdenDeviceDataProvider? _nihonKohden;

    private readonly Dictionary<IDeviceDataProvider, List<DeviceState>> _deviceStates = new();
    private readonly IWatchDogService _watchDogService;
    private readonly List<IDeviceDataProvider> _deviceDataProviders = new();

    /// <param name="nihonKohden">Nihon Kohden monitors to dial; none when omitted or empty.</param>
    public DeviceService(
        IWatchDogService watchDogService,
        ILoggerFactory? loggerFactory = null,
        NihonKohdenOptions? nihonKohden = null)
    {
        _watchDogService = watchDogService;
        UmecDeviceDataProvider umecDeviceDataProvider =
            new(loggerFactory?.CreateLogger<UmecDeviceDataProvider>());
        _watchDogService.OnDeviceStateChanged += OnDeviceStateChanged;
        _deviceDataProviders.Add(umecDeviceDataProvider);

        // A Nihon Kohden monitor speaks HL7 or LS-NET, whichever is selected on it; each has its own driver.
        NihonKohdenDeviceDataProvider? nihonKohdenProvider = null;
        NihonKohdenLsNetProvider? lsNetProvider = null;
        if (nihonKohden is { Monitors.Count: > 0 })
        {
            if (nihonKohden.Monitors.Any(m => !m.IsLsNet))
            {
                var hl7 = new NihonKohdenOptions
                {
                    Monitors = nihonKohden.Monitors.Where(m => !m.IsLsNet).ToList(),
                    ReconnectDelayMs = nihonKohden.ReconnectDelayMs,
                    ReadTimeoutSeconds = nihonKohden.ReadTimeoutSeconds,
                };
                nihonKohdenProvider = new(hl7, loggerFactory?.CreateLogger<NihonKohdenDeviceDataProvider>());
                _deviceDataProviders.Add(nihonKohdenProvider);
            }

            if (nihonKohden.Monitors.Any(m => m.IsLsNet))
            {
                lsNetProvider = new(nihonKohden.LsNet, nihonKohden.Monitors, loggerFactory?.CreateLogger<NihonKohdenLsNetProvider>());
                _deviceDataProviders.Add(lsNetProvider);
            }
        }

        foreach (var provider in _deviceDataProviders){
            provider.OnDeviceConnected += OnDeviceConnected;
            provider.OnDeviceDisconnected += OnDeviceDisconnected;
            provider.OnMessageReceived += OnProviderMessageReceived;
        }

        // Dialled from configuration, not found by the watchdog; only once the handlers are in place.
        _nihonKohden = nihonKohdenProvider;
        nihonKohdenProvider?.Start();
        lsNetProvider?.Start();
    }

    /// <summary>Only Nihon Kohden monitors take a patient over the network; a uMec is told nothing.</summary>
    public Task<MonitorAdmissionResult> AssignPatientAsync(
        string monitorIp, string patientId, string patientName, CancellationToken cancellationToken = default) =>
        _nihonKohden is { } provider && provider.Owns(monitorIp)
            ? provider.AssignPatientAsync(monitorIp, patientId, patientName, cancellationToken)
            : NotSupported;

    public Task<MonitorAdmissionResult> UpdatePatientAsync(
        string monitorIp, string patientId, string patientName, CancellationToken cancellationToken = default) =>
        _nihonKohden is { } provider && provider.Owns(monitorIp)
            ? provider.UpdatePatientAsync(monitorIp, patientId, patientName, cancellationToken)
            : NotSupported;

    private void OnProviderMessageReceived(UmecMessage message)
    {
        OnMessageReceived?.Invoke(message);
    }
    private void OnDeviceStateChanged(DeviceState deviceState)
    {
        if (deviceState.State == DeviceStateEnum.Connected)
        {
            foreach (var deviceDataProvider in _deviceDataProviders)
            {
                deviceDataProvider.Connect(deviceState);
            }
        }
        else if (deviceState.State == DeviceStateEnum.Disconnected)
        {
            foreach (var pair in _deviceStates)
            {
                var provider = pair.Key;
                var states = pair.Value;

                var connectedDevice = states.FirstOrDefault(
                    state => state.IpAddress == deviceState.IpAddress);

                if (connectedDevice is null)
                {
                    continue;
                }

                provider.Disconnect(deviceState);
                states.Remove(connectedDevice);

                break;
            }
        }
    }
    private void OnDeviceConnected(IDeviceDataProvider sender, DeviceState deviceState)
    {

        if (!_deviceStates.TryGetValue(sender, out var states))
        {
            states = new List<DeviceState>();
            _deviceStates[sender] = states;
        }

        if (states.All(state => state.IpAddress != deviceState.IpAddress))
        {
            states.Add(deviceState);
        }
    }

    private void OnDeviceDisconnected(IDeviceDataProvider sender, DeviceState deviceState)
    {
        if (_deviceStates.TryGetValue(sender, out var states))
        {
            states.RemoveAll(state => state.IpAddress == deviceState.IpAddress);
        }
    }
    public List<DeviceState> GetAllDevice(){
        List<DeviceState> devices = new();
        foreach (var pair in _deviceStates){
            devices.AddRange(pair.Value);
        }
        return devices;
    }
}