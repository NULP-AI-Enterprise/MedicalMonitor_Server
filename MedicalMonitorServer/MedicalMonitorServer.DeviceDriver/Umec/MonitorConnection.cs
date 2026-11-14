using MedicalMonitorServer.Contracts.Models;
using Microsoft.Extensions.Logging;

namespace MedicalMonitorServer.DeviceDriver.Umec;

/// <summary>
/// One monitor's HL7 link, kept up for as long as the provider wants it: every
/// broken or failed session is logged and retried after
/// <see cref="UmecLinkOptions.ReconnectDelay"/>. After
/// <see cref="UmecLinkOptions.MaxFailedSessions"/> attempts that carried no
/// data the connection gives up and reports itself closed.
/// </summary>
/// <remarks>
/// The reader and the parser are injected so the retry, echo and give-up
/// behaviour can be driven without a monitor on the network.
/// </remarks>
internal sealed class MonitorConnection : IDisposable
{
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly IComunicationService _communicationService;
    private readonly IParserService _parserService;
    private readonly Action<UmecMessage> _messageReceived;
    private readonly Action<MonitorConnection, bool> _closed;
    private readonly UmecLinkOptions _options;
    private readonly ILogger _logger;
    private readonly int _port;
    private int _messagesThisSession;
    private int _disposed;

    public MonitorConnection(
        DeviceState state,
        int port,
        Action<UmecMessage> messageReceived,
        Action<MonitorConnection, bool> closed,
        IComunicationService communicationService,
        IParserService parserService,
        UmecLinkOptions options,
        ILogger logger)
    {
        State = state;
        _port = port;
        _messageReceived = messageReceived;
        _closed = closed;
        _communicationService = communicationService;
        _parserService = parserService;
        _options = options;
        _logger = logger;
    }

    public DeviceState State { get; }

    /// <summary>
    /// The run loop, so a caller can wait for the link to finish. Completed
    /// until <see cref="Start"/> has been called.
    /// </summary>
    public Task Completion { get; private set; } = Task.CompletedTask;

    public void Start()
    {
        Completion = RunAsync();
    }

    private async Task RunAsync()
    {
        var cancellationToken = _cancellationTokenSource.Token;
        int failedSessions = 0;

        try
        {
            while (!cancellationToken.IsCancellationRequested && failedSessions < _options.MaxFailedSessions)
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
                    await Task.Delay(_options.ReconnectDelay, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            if (failedSessions >= _options.MaxFailedSessions)
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

        using var echoTimer = new PeriodicTimer(_options.EchoInterval);
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

                // Most messages carry no patient id; the feed attributes them by the monitor they came from.
                message.SourceIp = State.IpAddress;
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
