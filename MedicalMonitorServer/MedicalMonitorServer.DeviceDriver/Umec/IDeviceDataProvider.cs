using MedicalMonitorServer.Contracts.Models;

namespace MedicalMonitorServer.DeviceDriver.Umec;

public interface IDeviceDataProvider : IDisposable
{
    event Action<IDeviceDataProvider, DeviceState>? OnDeviceConnected;

    event Action<UmecMessage>? OnMessageReceived;

    IReadOnlyCollection<DeviceState> ConnectedDevices { get; }

    IReadOnlyCollection<PatientData> Patients { get; }

    void Connect(DeviceState deviceState);

    void Disconnect(DeviceState deviceState);
}
