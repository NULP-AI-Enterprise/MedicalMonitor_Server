using MedicalMonitorServer.Contracts.Models;
using MedicalMonitorServer.DeviceDriver;
using MedicalMonitorServer.DeviceDriver.Umec;
using Microsoft.Extensions.Logging;

namespace MedicalMonitorServer.Tests;

/// <summary>A watchdog that never scans; tests raise its transitions by hand.</summary>
internal sealed class FakeWatchDog : IWatchDogService
{
    public Action<DeviceState>? OnDeviceStateChanged { get; set; }

    public Func<string, bool>? IsLinkOwned { get; set; }

    public Task StartWatchDog(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>A device provider that delivers the messages a test hands it.</summary>
internal sealed class FakeProvider : IDeviceDataProvider
{
#pragma warning disable CS0067 // link state is not what these tests are about
    public event Action<IDeviceDataProvider, DeviceState>? OnDeviceConnected;

    public event Action<IDeviceDataProvider, DeviceState>? OnDeviceDisconnected;
#pragma warning restore CS0067

    public event Action<UmecMessage>? OnMessageReceived;

    public IReadOnlyCollection<DeviceState> ConnectedDevices => Array.Empty<DeviceState>();

    public IReadOnlyCollection<PatientInfo> Patients => Array.Empty<PatientInfo>();

    public void Connect(DeviceState deviceState) { }

    public void Disconnect(DeviceState deviceState) { }

    public void Deliver(UmecMessage message) => OnMessageReceived?.Invoke(message);

    public void Dispose() { }
}

/// <summary>Keeps every log entry, formatted, so a test can assert what an operator would read.</summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly List<(LogLevel Level, string Message)> _entries = new();

    public IReadOnlyList<(LogLevel Level, string Message)> Entries
    {
        get { lock (_entries) { return _entries.ToArray(); } }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (_entries) { _entries.Add((logLevel, formatter(state, exception))); }
    }
}

/// <summary>Polls a condition, so a test waits no longer than it must and never hangs.</summary>
internal static class Wait
{
    public static async Task UntilAsync(Func<bool> condition, string because, int timeoutMs = 10_000)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return;
            await Task.Delay(5);
        }

        throw new TimeoutException($"Waited {timeoutMs}ms for: {because}.");
    }
}
