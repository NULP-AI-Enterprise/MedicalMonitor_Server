using MedicalMonitorServer.Contracts.Models;

namespace MedicalMonitorServer.DeviceDriver.Umec;

public interface IUmecProvider
{
    IReadOnlyCollection<DeviceState> GetDevices();

    bool TryGetDevice(int deviceId, out DeviceState? device);

    DeviceState RegisterDevice(DeviceState device);

    bool RemoveDevice(int deviceId);

    bool UpdateState(int deviceId, DeviceStateEnum state);
}