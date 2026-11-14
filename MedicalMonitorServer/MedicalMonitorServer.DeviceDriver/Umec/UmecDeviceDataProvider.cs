using System.Collections.Concurrent;
using MedicalMonitorServer.Contracts.Models;
using MedicalMonitorServer.DeviceDriver;

namespace MedicalMonitorServer.DeviceDriver.Umec;

public sealed class UmecDeviceDataProvider : IDeviceDataProvider
{
    private readonly ConcurrentDictionary<string, MonitorConnection> _connections = new();
    private readonly ConcurrentDictionary<string, PatientData> _patients = new();

    public event Action<IDeviceDataProvider, DeviceState>? OnDeviceConnected;

    public event Action<IDeviceDataProvider, DeviceState>? OnDeviceDisconnected;

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

        var connection = new MonitorConnection(
            deviceState,
            port,
            OnMessage,
            state => OnDeviceConnected?.Invoke(this, state),
            OnConnectionEnded);
        if (_connections.TryAdd(deviceState.IpAddress, connection))
        {
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
        var patient = message.Oru?.Patient ?? message.Adt?.Patient;
        if (patient is not null && !string.IsNullOrWhiteSpace(patient.PatientId))
        {
            _patients[patient.PatientId] = new PatientData
            {
                PatientId = patient.PatientId,
                Name = patient.FullName,
                Values = new Dictionary<string, string>
                {
                    ["bedNumber"] = patient.BedNumber,
                    ["department"] = patient.Department,
                },
            };
        }

        OnMessageReceived?.Invoke(message);
    }

    private void OnConnectionEnded(MonitorConnection connection, bool wasConnected)
    {
        if (_connections.TryGetValue(connection.State.IpAddress, out var current) &&
            ReferenceEquals(current, connection))
        {
            _connections.TryRemove(connection.State.IpAddress, out _);
        }

        if (wasConnected)
        {
            OnDeviceDisconnected?.Invoke(this, connection.State);
        }
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
        private readonly Action<DeviceState> _connected;
        private readonly Action<MonitorConnection, bool> _ended;
        private readonly int _port;

        public MonitorConnection(
            DeviceState state,
            int port,
            Action<UmecMessage> messageReceived,
            Action<DeviceState> connected,
            Action<MonitorConnection, bool> ended)
        {
            State = state;
            _port = port;
            _parserService = new ParserService();
            _messageReceived = messageReceived;
            _connected = connected;
            _ended = ended;
        }

        public DeviceState State { get; }

        public void Start()
        {
            _ = RunAsync();
        }

        private async Task RunAsync()
        {
            var wasConnected = false;
            try
            {
                await _communicationService.ReadTcpStreamAsync(
                    State.IpAddress,
                    _port,
                    Parse,
                    cancellationToken =>
                    {
                        wasConnected = true;
                        _connected(State);
                        return Task.CompletedTask;
                    },
                    _cancellationTokenSource.Token);
            }
            catch (OperationCanceledException) when (_cancellationTokenSource.IsCancellationRequested)
            {
            }
            finally
            {
                _ended(this, wasConnected);
            }
        }

        private Task Parse(byte[] data, CancellationToken cancellationToken)
        {
            var message = _parserService.Parse(data);
            if (message is not null)
            {
                _messageReceived(message);
            }

            return Task.CompletedTask;
        }

        public void Dispose()
        {
            _cancellationTokenSource.Cancel();
            _cancellationTokenSource.Dispose();
        }
    }
}
