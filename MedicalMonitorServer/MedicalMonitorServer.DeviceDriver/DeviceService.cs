namespace MedicalMonitorServer.DeviceDriver;

public class DeviceService : IDeviceService
{
    private readonly IWatchDogService _watchDogService;

    public DeviceService(IWatchDogService watchDogService)
    {
        _watchDogService = watchDogService;
    }
}