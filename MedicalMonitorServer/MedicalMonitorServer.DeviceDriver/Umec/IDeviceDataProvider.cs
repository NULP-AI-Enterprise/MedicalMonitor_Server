using MedicalMonitorServer.Contracts.Models;

namespace MedicalMonitorServer.DeviceDriver.Umec;

public interface IDeviceDataProvider
{
    DeviceData Add(DeviceData data);

    bool TryGetLatest(int deviceId, out DeviceData? data);

    IReadOnlyCollection<DeviceData> GetLatest();

    bool Remove(int deviceId);
}