using System.Collections.Concurrent;
using MedicalMonitorServer.Contracts.Models;

namespace MedicalMonitorServer.DeviceDriver.Umec;

public sealed class UmecDeviceDataProvider : IDeviceDataProvider
{
    private readonly ConcurrentDictionary<int, DeviceData> _latestData = new();

    public DeviceData Add(DeviceData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (data.DeviceId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(data), "Device id must be greater than zero.");
        }

        if (data.Timestamp == default)
        {
            throw new ArgumentException("A data timestamp is required.", nameof(data));
        }

        var copy = new DeviceData
        {
            DeviceId = data.DeviceId,
            Timestamp = data.Timestamp,
            Values = new Dictionary<string, string>(data.Values)
        };

        _latestData.AddOrUpdate(
            data.DeviceId,
            copy,
            (_, existing) => existing.Timestamp >= copy.Timestamp ? existing : copy);

        return copy;
    }

    public bool TryGetLatest(int deviceId, out DeviceData? data) =>
        _latestData.TryGetValue(deviceId, out data);

    public IReadOnlyCollection<DeviceData> GetLatest() =>
        _latestData.Values.ToArray();

    public bool Remove(int deviceId) => _latestData.TryRemove(deviceId, out _);
}