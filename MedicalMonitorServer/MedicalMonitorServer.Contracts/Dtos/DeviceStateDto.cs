namespace MedicalMonitorServer.Contracts.Dtos;

public sealed class DeviceStateDto
{
    public int Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public string IpAddress { get; init; } = string.Empty;

    public string Port { get; init; } = string.Empty;

    public DeviceStateEnumDto State { get; init; }
}
