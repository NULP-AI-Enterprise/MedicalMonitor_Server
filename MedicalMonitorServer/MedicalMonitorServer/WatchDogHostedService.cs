using MedicalMonitorServer.DeviceDriver;

namespace MedicalMonitorServer;

public sealed class WatchDogHostedService : BackgroundService
{
    private readonly IWatchDogService _watchDogService;

    public WatchDogHostedService(IWatchDogService watchDogService, DeviceService deviceService)
    {
        _watchDogService = watchDogService;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        return _watchDogService.StartWatchDog(stoppingToken);
    }
}
