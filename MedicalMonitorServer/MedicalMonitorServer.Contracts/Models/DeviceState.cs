namespace MedicalMonitorServer.Contracts.Models;

public class DeviceState
{
    public int Id { get; set; }
    
    public string Name { get; set; }

    public string IpAddress { get; set; }

    public string Port { get; set; }

    public DeviceStateEnum State { get; set; }
}

public enum DeviceStateEnum
{
    Disconnected = 0,
    Connected = 1,
    Error = 2
}