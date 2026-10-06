using MedicalMonitorServer.Contracts.Models;

namespace MedicalMonitorServer.DeviceDriver.Umec;

public interface IDeviceDataProvider
{
    event Action<IDeviceDataProvider, DeviceState> OnDeviceConnected;
    event Action<UmecMessage> OnMessageReceived;

    void Connect(DeviceState deviceState);
    void Disconnect(DeviceState deviceState);

}