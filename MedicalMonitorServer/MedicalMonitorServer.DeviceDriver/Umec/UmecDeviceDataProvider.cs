using System.Collections.Concurrent;
using MedicalMonitorServer.Contracts.Models;
using MedicalMonitorServer.DeviceDriver;

namespace MedicalMonitorServer.DeviceDriver.Umec;

public sealed class UmecDeviceDataProvider : IDeviceDataProvider
{
    private readonly ConcurrentDictionary<string, MonitorConnection> _connections = new();
    private readonly ConcurrentDictionary<string, PatientData> _patients = new();

    public event Action<IDeviceDataProvider, DeviceState>? OnDeviceConnected;

    public event Action<UmecMessage>? OnMessageReceived;

    public IReadOnlyCollection<DeviceState> ConnectedDevices =>
        _connections.Values.Select(connection => connection.State).ToArray();

    public IReadOnlyCollection<PatientData> Patients => _patients.Values.ToArray();

    public void Connect(DeviceState deviceState)
    {
        ArgumentNullException.ThrowIfNull(deviceState);

        if (string.IsNullOrWhiteSpace(deviceState.IpAddress) ||
            !int.TryParse(deviceState.Port, out var port))
        {
            throw new ArgumentException("A valid monitor IP address and port are required.", nameof(deviceState));
        }

        if (_connections.ContainsKey(deviceState.IpAddress))
        {
            return;
        }

        var connection = new MonitorConnection(deviceState, port, OnMessage);
        if (_connections.TryAdd(deviceState.IpAddress, connection))
        {
            OnDeviceConnected?.Invoke(this, deviceState);
            connection.Start();
        }
        else
        {
            connection.Dispose();
        }
    }

    public void Disconnect(DeviceState deviceState)
    {
        ArgumentNullException.ThrowIfNull(deviceState);

        if (_connections.TryRemove(deviceState.IpAddress, out var connection))
        {
            connection.Dispose();
        }
    }

    private void OnMessage(UmecMessage message)
    {
        if (message.Patient is not null && !string.IsNullOrWhiteSpace(message.Patient.PatientId))
        {
            _patients[message.Patient.PatientId] = message.Patient;
        }

        OnMessageReceived?.Invoke(message);
    }

    public void Dispose()
    {
        foreach (var connection in _connections.Values)
        {
            connection.Dispose();
        }

        _connections.Clear();
    }

    private sealed class MonitorConnection : IDisposable
    {
        private readonly CancellationTokenSource _cancellationTokenSource = new();
        private readonly ComunicationService _communicationService = new();
        private readonly ParserService _parserService;
        private readonly Action<UmecMessage> _messageReceived;
        private readonly int _port;

        public MonitorConnection(DeviceState state, int port, Action<UmecMessage> messageReceived)
        {
            State = state;
            _port = port;
            _parserService = new ParserService(state);
            _messageReceived = messageReceived;
            _communicationService.DataReceived += Parse;
        }

        public DeviceState State { get; }

        public void Start()
        {
            _ = RunAsync();
        }

        private async Task RunAsync()
        {
            try
            {
                await _communicationService.ConnectAsync(
                    State.IpAddress,
                    _port,
                    _cancellationTokenSource.Token);
            }
            catch (OperationCanceledException) when (_cancellationTokenSource.IsCancellationRequested)
            {
            }
        }

        private Task Parse(ReadOnlyMemory<byte> data)
        {
            foreach (var message in _parserService.Parse(data.Span))
            {
                _messageReceived(message);
            }

            return Task.CompletedTask;
        }

        public void Dispose()
        {
            _cancellationTokenSource.Cancel();
            _communicationService.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _cancellationTokenSource.Dispose();
        }
    }
}
