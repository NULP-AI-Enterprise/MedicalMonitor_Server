namespace MedicalMonitorServer.Contracts.Dtos;

public sealed class MonitorInfoDto
{
    public string Name { get; init; } = string.Empty;

    public bool? Standby { get; init; }

    public int? HighestAlarmType { get; init; }

    public bool? HighestAlarmConfirmed { get; init; }

    public int? FreeConnections { get; init; }

    public int? IpSequence { get; init; }

    public string TelemetrySerial { get; init; } = string.Empty;

    public string DeviceId { get; init; } = string.Empty;

    public string MachineType { get; init; } = string.Empty;

    public string MachineVersion { get; init; } = string.Empty;

    public string ViewBedDeviceId { get; init; } = string.Empty;

    public int? ViewBedIdLength { get; init; }
}
