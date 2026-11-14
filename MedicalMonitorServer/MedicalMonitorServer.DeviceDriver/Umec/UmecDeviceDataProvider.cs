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
    private readonly ILogger _logger;

    public UmecDeviceDataProvider(ILogger<UmecDeviceDataProvider>? logger = null)
    {
        _logger = logger ?? NullLogger<UmecDeviceDataProvider>.Instance;
    }

    public event Action<IDeviceDataProvider, DeviceState>? OnDeviceConnected;

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

        var connection = new MonitorConnection(deviceState, port, OnMessage, OnConnectionClosed, _logger);
        if (_connections.TryAdd(deviceState.IpAddress, connection))
        {
            _logger.LogInformation("Opening the HL7 link to monitor {Ip}:{Port}.", deviceState.IpAddress, port);
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

    private sealed class MonitorConnection : IDisposable
    {
        /// <summary>Pause before dialling the monitor again after a failed session.</summary>
        private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(2);

        /// <summary>
        /// The monitor wants to hear from us at least every 10 seconds; half
        /// that leaves room for one lost echo.
        /// </summary>
        private static readonly TimeSpan EchoInterval = TimeSpan.FromSeconds(5);

        /// <summary>Consecutive sessions without a single message after which this connection gives up.</summary>
        private const int MaxFailedSessions = 5;

        private readonly CancellationTokenSource _cancellationTokenSource = new();
        private readonly ComunicationService _communicationService;
        private readonly ParserService _parserService;
        private readonly Action<UmecMessage> _messageReceived;
        private readonly Action<MonitorConnection, bool> _closed;
        private readonly ILogger _logger;
        private readonly int _port;
        private int _messagesThisSession;
        private int _disposed;

        public MonitorConnection(
            DeviceState state,
            int port,
            Action<UmecMessage> messageReceived,
            Action<MonitorConnection, bool> closed,
            ILogger logger)
        {
            State = state;
            _port = port;
            _parserService = new ParserService();
            _communicationService = new ComunicationService(logger);
            _messageReceived = messageReceived;
            _closed = closed;
            _logger = logger;
        }

        public DeviceState State { get; }

        public void Start()
        {
            _ = RunAsync();
        }

        /// <summary>
        /// Keeps the HL7 link up: every broken or failed session is logged and
        /// retried after <see cref="ReconnectDelay"/>. After
        /// <see cref="MaxFailedSessions"/> attempts that carried no data the
        /// connection gives up and reports itself closed.
        /// </summary>
        private async Task RunAsync()
        {
            var cancellationToken = _cancellationTokenSource.Token;
            int failedSessions = 0;

            try
            {
                while (!cancellationToken.IsCancellationRequested && failedSessions < MaxFailedSessions)
                {
                    Interlocked.Exchange(ref _messagesThisSession, 0);

                    try
                    {
                        await _communicationService.ReadTcpStreamAsync(
                            State.IpAddress,
                            _port,
                            Parse,
                            RunSessionAsync,
                            cancellationToken);

                        if (cancellationToken.IsCancellationRequested)
                        {
                            break;
                        }

                        _logger.LogWarning("Monitor {Ip}:{Port} ended the HL7 stream after {Count} message(s).",
                            State.IpAddress, _port, Volatile.Read(ref _messagesThisSession));
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "HL7 link to monitor {Ip}:{Port} failed after {Count} message(s).",
                            State.IpAddress, _port, Volatile.Read(ref _messagesThisSession));
                    }

                    // A session that carried data proves the monitor is there,
                    // so only fruitless attempts count towards giving up.
                    failedSessions = Volatile.Read(ref _messagesThisSession) > 0 ? 0 : failedSessions + 1;

                    try
                    {
                        await Task.Delay(ReconnectDelay, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }

                if (failedSessions >= MaxFailedSessions)
                {
                    _logger.LogError("Monitor {Ip}:{Port} delivered nothing in {Attempts} attempts; giving up.",
                        State.IpAddress, _port, failedSessions);
                }
            }
            finally
            {
                _closed(this, cancellationToken.IsCancellationRequested);
                _cancellationTokenSource.Dispose();
            }
        }

        /// <summary>
        /// Runs for as long as the stream is up: asks the monitor to start
        /// sending vitals and alarms, then keeps the link alive with the echo
        /// it expects. Cancelled by the reader as soon as the stream ends.
        /// </summary>
        private async Task RunSessionAsync(IMonitorLink link, CancellationToken cancellationToken)
        {
            // Without this query the monitor only emits its own echo.
            await link.SendAsync(UmecRequests.StartDataQuery, cancellationToken);
            _logger.LogInformation("Asked monitor {Ip}:{Port} to start sending (QRY^R02, id 1203).",
                State.IpAddress, _port);

            using var echoTimer = new PeriodicTimer(EchoInterval);
            while (await echoTimer.WaitForNextTickAsync(cancellationToken))
            {
                await link.SendAsync(UmecRequests.Echo, cancellationToken);
            }
        }

        private Task Parse(byte[] data, CancellationToken cancellationToken)
        {
            try
            {
                var message = _parserService.Parse(data);
                if (message is null)
                {
                    _logger.LogDebug("Monitor {Ip}:{Port}: {Bytes} bytes that are not an HL7 message.",
                        State.IpAddress, _port, data.Length);
                }
                else
                {
                    Interlocked.Increment(ref _messagesThisSession);
                    _messageReceived(message);
                }
            }
            catch (Exception ex)
            {
                // One message the parser or a subscriber chokes on must not
                // take the whole connection down with it.
                _logger.LogError(ex, "Monitor {Ip}:{Port}: dropped one message ({Bytes} bytes).",
                    State.IpAddress, _port, data.Length);
            }

            return Task.CompletedTask;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                _cancellationTokenSource.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The run loop already finished and cleaned up after itself.
            }
        }
    }
}
