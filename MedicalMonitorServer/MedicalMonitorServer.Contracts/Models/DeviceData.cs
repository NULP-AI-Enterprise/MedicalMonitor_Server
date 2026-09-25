namespace MedicalMonitorServer.Contracts.Models;

public sealed class DeviceData
{
    public int DeviceId { get; init; }

    public DateTimeOffset Timestamp { get; init; }

    public IReadOnlyDictionary<string, string> Values { get; init; } =
        new Dictionary<string, string>();
}