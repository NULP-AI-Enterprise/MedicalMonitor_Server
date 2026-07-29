using MedicalMonitorServer.Contracts.Models;

namespace MedicalMonitorServer.DeviceDriver;

public class WatchDogService : IWatchDogService
{
    public Action<DeviceState> OnDeviceStateChanged { get; set; }

    public void StartWatchDog()
    {
        // Start watchdog logic here
    }
}