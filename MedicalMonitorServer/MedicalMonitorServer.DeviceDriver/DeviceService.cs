using MedicalMonitorServer.Contracts.Models;
using MedicalMonitorServer.DeviceDriver.Umec;
using Microsoft.Extensions.Logging;

namespace MedicalMonitorServer.DeviceDriver;

public class DeviceService : IDeviceService
{
    public event Action<UmecMessage>? OnMessageReceived;

    private readonly Dictionary<IDeviceDataProvider, List<DeviceState>> _deviceStates = new();
    private readonly IWatchDogService _watchDogService;
    private readonly List<IDeviceDataProvider> _deviceDataProviders = new();

    /// <param name="linkOptions">Link timings handed to the uMec provider; defaults match a real monitor.</param>
    /// <param name="providers">Substituted by tests; by default the uMec provider alone.</param>
    public DeviceService(
        IWatchDogService watchDogService,
        ILoggerFactory? loggerFactory = null,
        UmecLinkOptions? linkOptions = null,
        IEnumerable<IDeviceDataProvider>? providers = null)
    {
        _watchDogService = watchDogService;

        _deviceDataProviders.AddRange(providers ?? new IDeviceDataProvider[]
        {
            new UmecDeviceDataProvider(
                loggerFactory?.CreateLogger<UmecDeviceDataProvider>(), linkOptions),
        });

        foreach (var provider in _deviceDataProviders){
            provider.OnDeviceConnected += OnDeviceConnected;
            provider.OnDeviceDisconnected += OnDeviceDisconnected;
            provider.OnMessageReceived += OnProviderMessageReceived;
        }

        // Wired last, so a scan can never see a half-built service: with the
        // provider list still empty a Connected edge would be swallowed, and the
        // watchdog reports only *changes*, so that monitor would stay offline
        // until it happened to flap.
        _watchDogService.OnDeviceStateChanged += OnDeviceStateChanged;
        _watchDogService.IsLinkOwned = IsLinkOwned;
    }

    /// <summary>
    /// Whether a driver already holds this monitor's HL7 link, so the watchdog must
    /// not probe the port that link is using.
    /// </summary>
    /// <remarks>
    /// Reads <see cref="IDeviceDataProvider.ConnectedDevices"/>, a snapshot of the
    /// provider's own concurrent dictionary, which is populated from
    /// <c>Connect()</c> until the link gives up — i.e. across every reconnect
    /// attempt, not just while a session happens to be up.
    /// Deliberately NOT <see cref="_deviceStates"/> or <see cref="GetAllDevice"/>:
    /// those are a plain <see cref="Dictionary{TKey,TValue}"/> mutated from link
    /// threads, so reading them from the watchdog loop would be a data race. Only
    /// <see cref="DeviceState.IpAddress"/> is read — never <c>State</c>, which is
    /// the unsynchronised field.
    /// </remarks>
    private bool IsLinkOwned(string ipAddress) =>
        _deviceDataProviders.Any(provider =>
            provider.ConnectedDevices.Any(device => device.IpAddress == ipAddress));

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