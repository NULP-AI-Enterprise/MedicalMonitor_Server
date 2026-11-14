using System.Collections.Concurrent;
using MedicalMonitorServer.Contracts.Models;
using MedicalMonitorServer.DeviceDriver;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MedicalMonitorServer.DeviceDriver.Umec;

public sealed class UmecDeviceDataProvider : IDeviceDataProvider
{
    private readonly ConcurrentDictionary<string, MonitorConnection> _connections = new();
    private readonly ConcurrentDictionary<string, PatientInfo> _patients = new();
    private readonly Func<ILogger, IComunicationService> _readerFactory;
    private readonly IParserService _parserService;
    private readonly UmecLinkOptions _options;
    private readonly ILogger _logger;

    /// <param name="logger">Logger for the provider and every link it opens.</param>
    /// <param name="options">Link timings; the defaults match a real uMec10.</param>
    /// <param name="readerFactory">
    /// Makes the reader for one link. One instance per connection, because a
    /// reader holds that connection's half-received frame. Tests substitute
    /// this to drive a link without a monitor on the network.
    /// </param>
    /// <param name="parserService">Decoder for received messages; stateless, so one is shared.</param>
    public UmecDeviceDataProvider(
        ILogger<UmecDeviceDataProvider>? logger = null,
        UmecLinkOptions? options = null,
        Func<ILogger, IComunicationService>? readerFactory = null,
        IParserService? parserService = null)
    {
        _logger = logger ?? NullLogger<UmecDeviceDataProvider>.Instance;
        _options = options ?? new UmecLinkOptions();
        _readerFactory = readerFactory ?? (log => new ComunicationService(log));
        _parserService = parserService ?? new ParserService();
    }

    /// <summary>Raised when a monitor's TCP stream is actually open (again after every reconnect).</summary>
    public event Action<IDeviceDataProvider, DeviceState>? OnDeviceConnected;

    /// <summary>Raised when an open stream to a monitor ends, whoever ended it.</summary>
    public event Action<IDeviceDataProvider, DeviceState>? OnDeviceDisconnected;

    public event Action<UmecMessage>? OnMessageReceived;

    public IReadOnlyCollection<DeviceState> ConnectedDevices =>
        _connections.Values.Select(connection => connection.State).ToArray();

    public IReadOnlyCollection<PatientInfo> Patients => _patients.Values.ToArray();

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
            OnConnectionClosed,
            _readerFactory(_logger),
            _parserService,
            _options,
            _logger,
            state => OnDeviceConnected?.Invoke(this, state),
            state => OnDeviceDisconnected?.Invoke(this, state));

        if (_connections.TryAdd(deviceState.IpAddress, connection))
        {
            _logger.LogInformation("Opening the HL7 link to monitor {Ip}:{Port}.", deviceState.IpAddress, port);
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
            _logger.LogInformation("Closing the HL7 link to monitor {Ip}.", deviceState.IpAddress);
            connection.Dispose();
        }
    }

    /// <summary>
    /// A connection reports itself here once its run loop is over. Leaving it
    /// in <see cref="_connections"/> would keep the monitor offline for good,
    /// because <see cref="Connect"/> skips an address that is already there.
    /// </summary>
    /// <param name="cancelled">True when we closed the link ourselves.</param>
    private void OnConnectionClosed(MonitorConnection connection, bool cancelled)
    {
        var state = connection.State;
        _connections.TryRemove(new KeyValuePair<string, MonitorConnection>(state.IpAddress, connection));

        if (cancelled)
        {
            return;
        }

        // The watchdog only reports state *changes*, so a monitor it still
        // considers connected would never be handed to us again. Marking it
        // disconnected makes its next successful probe do exactly that.
        state.State = DeviceStateEnum.Disconnected;

        _logger.LogWarning("Dropped the HL7 link to monitor {Ip}:{Port}; waiting for the watchdog to see it again.",
            state.IpAddress, state.Port);
    }

    private void OnMessage(UmecMessage message)
    {
        var patient = message.Oru?.Patient ?? message.Adt?.Patient;
        if (patient is not null && !string.IsNullOrWhiteSpace(patient.PatientId))
        {
            _patients[patient.PatientId] = patient;
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
}
