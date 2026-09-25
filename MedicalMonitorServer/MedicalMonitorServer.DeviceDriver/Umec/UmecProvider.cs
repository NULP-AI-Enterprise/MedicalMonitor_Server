using System.Collections.Concurrent;
using MedicalMonitorServer.Contracts.Models;

namespace MedicalMonitorServer.DeviceDriver.Umec;

public sealed class UmecProvider : IUmecProvider
{
    private readonly ConcurrentDictionary<int, DeviceState> _devices = new();

    public IReadOnlyCollection<DeviceState> GetDevices() =>
        _devices.Values.ToArray();

    public bool TryGetDevice(int deviceId, out DeviceState? device) =>
        _devices.TryGetValue(deviceId, out device);

    public DeviceState RegisterDevice(DeviceState device)
    {
        ArgumentNullException.ThrowIfNull(device);

        if (device.Id <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(device), "Device id must be greater than zero.");
        }

        if (string.IsNullOrWhiteSpace(device.Name))
        {
            throw new ArgumentException("Device name is required.", nameof(device));
        }

        if (string.IsNullOrWhiteSpace(device.IpAddress))
        {
            throw new ArgumentException("Device IP address is required.", nameof(device));
        }

        if (string.IsNullOrWhiteSpace(device.Port))
        {
            throw new ArgumentException("Device port is required.", nameof(device));
        }

        if (!_devices.TryAdd(device.Id, device))
        {
            throw new InvalidOperationException($"Device {device.Id} is already registered.");
        }

        return device;
    }

    public bool RemoveDevice(int deviceId) => _devices.TryRemove(deviceId, out _);

    public bool UpdateState(int deviceId, DeviceStateEnum state)
    {
        while (_devices.TryGetValue(deviceId, out var current))
        {
            var updated = new DeviceState
            {
                Id = current.Id,
                Name = current.Name,
                IpAddress = current.IpAddress,
                Port = current.Port,
                State = state
            };

            if (_devices.TryUpdate(deviceId, updated, current))
            {
                return true;
            }
        }

        return false;
    }
}