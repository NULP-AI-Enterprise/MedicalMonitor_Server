namespace MedicalMonitorServer.DeviceDriver;
using MedicalMonitorServer.Contracts.Models;

public interface IWatchDogService
{
    Action<DeviceState>? OnDeviceStateChanged { get; set; }

    /// <summary>
    /// Asked before probing an address: true while a driver already holds that
    /// monitor's HL7 link. A monitor serves a limited number of HL7 clients on
    /// the port being probed, so probing one we are already connected to takes
    /// a slot for nothing, and a probe that then fails for any transient reason
    /// reports the monitor as gone and tears the working link down.
    /// Null (the default) probes every address, as before.
    /// </summary>
    Func<string, bool>? IsLinkOwned { get; set; }

    Task StartWatchDog(CancellationToken cancellationToken = default);
}