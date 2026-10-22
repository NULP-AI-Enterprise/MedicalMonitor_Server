namespace MedicalMonitorServer.DeviceDriver;
using MedicalMonitorServer.Contracts.Models;

public interface IWatchDogService
{
    Action<DeviceState>? OnDeviceStateChanged { get; set; }
    Task StartWatchDog(CancellationToken cancellationToken = default);
}