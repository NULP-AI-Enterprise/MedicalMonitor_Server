using MedicalMonitorServer.DeviceDriver;

namespace MedicalMonitorServer;

public sealed class WatchDogHostedService : BackgroundService
{
    private readonly IWatchDogService _watchDogService;

    /// <summary>
    /// Held only to keep this dependency: resolving <see cref="DeviceService"/>
    /// is what constructs the singleton, and its constructor is what subscribes
    /// to the watchdog and installs its link-ownership check. Dropping this
    /// parameter as "unused" would silently disable both — the build would stay
    /// green while the watchdog went back to probing ports the driver owns.
    /// </summary>
    private readonly DeviceService _deviceService;

    public WatchDogHostedService(IWatchDogService watchDogService, DeviceService deviceService)
    {
        _watchDogService = watchDogService;
        _deviceService = deviceService;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        return _watchDogService.StartWatchDog(stoppingToken);
    }
}
