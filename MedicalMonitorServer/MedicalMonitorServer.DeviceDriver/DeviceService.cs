using MedicalMonitorServer.Contracts.Models;
using MedicalMonitorServer.DeviceDriver.Umec;

namespace MedicalMonitorServer.DeviceDriver;

public class DeviceService : IDeviceService
{
    public event Action<UmecMessage>? OnMessageReceived;

    private readonly Dictionary<IDeviceDataProvider, List<DeviceState>> _deviceStates = new();
    private readonly IWatchDogService _watchDogService;
    private readonly List<IDeviceDataProvider> _deviceDataProviders = new();

    public DeviceService(IWatchDogService watchDogService)
    {
        _watchDogService = watchDogService;
        UmecDeviceDataProvider umecDeviceDataProvider = new();
        _watchDogService.OnDeviceStateChanged += OnDeviceStateChanged;
        _deviceDataProviders.Add(umecDeviceDataProvider);
        foreach (var provider in _deviceDataProviders){
            provider.OnDeviceConnected += OnDeviceConnected;
            provider.OnMessageReceived += OnProviderMessageReceived;
        }
    }

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

        states.Add(deviceState);
    }
    public List<DeviceState> GetAllDevice(){
        List<DeviceState> devices = new();
        foreach (var pair in _deviceStates){
            devices.AddRange(pair.Value);
        }
        return devices;
    }
}