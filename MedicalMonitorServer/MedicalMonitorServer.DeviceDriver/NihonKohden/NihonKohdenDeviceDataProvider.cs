using System.Globalization;
using System.Text;
using MedicalMonitorServer.Contracts.Models;
using MedicalMonitorServer.DeviceDriver.Umec;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MedicalMonitorServer.DeviceDriver.NihonKohden;

/// <summary>
/// Keeps one HL7 link per configured Nihon Kohden monitor. In HL7 mode the monitor is the
/// TCP server: we connect to its ORU port, it sends an ORU^R01 once a minute and wants each
/// one acknowledged. Unacknowledged, it repeats the message every 5 s and hangs up after the
/// third (seen on a BSM-3562, SW 07-41).
/// <para>
/// Unlike a uMec link, this one never gives up and is not handed over by the watchdog: the
/// monitors are listed in <see cref="NihonKohdenOptions"/> and dialled until the provider is
/// disposed.
/// </para>
/// </summary>
public sealed class NihonKohdenDeviceDataProvider : IDeviceDataProvider, IMonitorAdmissions
{
    /// <summary>Sends one message to a monitor port and returns its answer, or null when none came.</summary>
    public delegate Task<string?> AdtExchange(string ip, int port, string message, CancellationToken cancellationToken);

    private static readonly TimeSpan AdtTimeout = TimeSpan.FromSeconds(5);

    private readonly CancellationTokenSource _stopping = new();
    private readonly NihonKohdenOptions _options;
    private readonly Func<ILogger, IComunicationService> _readerFactory;
    private readonly IParserService _parser;
    private readonly AdtExchange _adtExchange;
    private readonly ILogger _logger;
    private readonly object _sync = new();
    private readonly List<DeviceState> _connected = new();

    // What each monitor last said about itself, by address: its bed name (PV1-3), which every
    // ADT message has to repeat, and the patient id it reports (PID-3, "" when it has none).
    private readonly Dictionary<string, (string BedName, string PatientId)> _reported = new(StringComparer.Ordinal);
    private int _adtSequence;
    private int _started;

    /// <param name="readerFactory">Makes the reader for one session; tests substitute it to drive a link without a monitor.</param>
    /// <param name="adtExchange">Carries one ADT message to a monitor; tests substitute it.</param>
    public NihonKohdenDeviceDataProvider(
        NihonKohdenOptions options,
        ILogger<NihonKohdenDeviceDataProvider>? logger = null,
        Func<ILogger, IComunicationService>? readerFactory = null,
        IParserService? parser = null,
        AdtExchange? adtExchange = null)
    {
        _options = options;
        _logger = logger ?? NullLogger<NihonKohdenDeviceDataProvider>.Instance;
        _readerFactory = readerFactory ?? (log => new ComunicationService(log)
        {
            ReadTimeout = TimeSpan.FromSeconds(options.ReadTimeoutSeconds),
        });
        _parser = parser ?? new NihonKohdenParser();
        _adtExchange = adtExchange ??
            ((ip, port, message, ct) => NihonKohdenAdt.ExchangeAsync(ip, port, message, AdtTimeout, ct));
    }

    public event Action<IDeviceDataProvider, DeviceState>? OnDeviceConnected;

    public event Action<IDeviceDataProvider, DeviceState>? OnDeviceDisconnected;

    public event Action<UmecMessage>? OnMessageReceived;

    public IReadOnlyCollection<DeviceState> ConnectedDevices
    {
        get { lock (_sync) { return _connected.ToArray(); } }
    }

    /// <summary>The monitor names no patient unless one was admitted on it by id; the roster handles that.</summary>
    public IReadOnlyCollection<PatientInfo> Patients => Array.Empty<PatientInfo>();

    /// <summary>Starts dialling every configured monitor. Subscribe to the events first.</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;

        foreach (var monitor in _options.Monitors)
        {
            if (string.IsNullOrWhiteSpace(monitor.Ip) || monitor.Port is < 1 or > 65535)
            {
                _logger.LogError("Nihon Kohden monitor \"{Name}\" has no usable address ({Ip}:{Port}); skipped.",
                    monitor.Name, monitor.Ip, monitor.Port);
                continue;
            }

            _ = RunAsync(monitor, _stopping.Token);
        }
    }

    /// <summary>Nothing to do: the watchdog's finds are uMec monitors, ours come from configuration.</summary>
    public void Connect(DeviceState deviceState)
    {
    }

    public void Disconnect(DeviceState deviceState)
    {
    }

    private async Task RunAsync(NihonKohdenMonitor monitor, CancellationToken cancellationToken)
    {
        var state = new DeviceState
        {
            Name = monitor.Name.Length > 0 ? monitor.Name : $"Nihon Kohden {monitor.Ip}",
            IpAddress = monitor.Ip,
            Port = monitor.Port.ToString(CultureInfo.InvariantCulture),
            State = DeviceStateEnum.Disconnected,
        };
        string? lastFailure = null;

        while (!cancellationToken.IsCancellationRequested)
        {
            // Completed once the stream is open; a message that arrives first waits for it.
            var link = new TaskCompletionSource<IMonitorLink>(TaskCreationOptions.RunContinuationsAsynchronously);
            int messages = 0;

            try
            {
                await _readerFactory(_logger).ReadTcpStreamAsync(
                    monitor.Ip,
                    monitor.Port,
                    async (data, ct) =>
                    {
                        await HandleAsync(state, data, await link.Task.WaitAsync(ct), ct);
                        messages++;
                    },
                    (opened, ct) =>
                    {
                        link.TrySetResult(opened);
                        MarkUp(state);
                        return Task.Delay(Timeout.Infinite, ct);
                    },
                    cancellationToken);

                if (cancellationToken.IsCancellationRequested) break;

                _logger.LogWarning("Nihon Kohden monitor {Ip}:{Port} ended the HL7 stream after {Count} message(s).",
                    monitor.Ip, monitor.Port, messages);
                lastFailure = null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A monitor that is switched off fails the same way every few seconds, for
                // hours; say it once.
                var level = ex.Message == lastFailure ? LogLevel.Debug : LogLevel.Warning;
                _logger.Log(level, "HL7 link to Nihon Kohden monitor {Ip}:{Port} failed after {Count} message(s): {Reason}",
                    monitor.Ip, monitor.Port, messages, ex.Message);
                lastFailure = ex.Message;
            }
            finally
            {
                MarkDown(state);
            }

            if (messages > 0) lastFailure = null;

            try
            {
                await Task.Delay(_options.ReconnectDelayMs, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task HandleAsync(DeviceState state, byte[] data, IMonitorLink link, CancellationToken cancellationToken)
    {
        var text = Encoding.Latin1.GetString(data);

        // Acknowledged before it is decoded: a message we cannot make sense of would
        // otherwise be repeated until the monitor closes the link.
        if (BuildAck(text, DateTime.Now) is { } ack)
        {
            await link.SendAsync(Encoding.ASCII.GetBytes(ack), cancellationToken);
        }

        try
        {
            var message = _parser.Parse(text);
            if (message is null)
            {
                _logger.LogDebug("Nihon Kohden monitor {Ip}: {Bytes} bytes that are not an HL7 message.",
                    state.IpAddress, data.Length);
                return;
            }

            message.SourceIp = state.IpAddress;
            if (message.Oru?.Patient is { BedLabel.Length: > 0 } patient)
            {
                lock (_sync) { _reported[state.IpAddress] = (patient.BedLabel, patient.PatientId.Trim()); }
            }

            OnMessageReceived?.Invoke(message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Nihon Kohden monitor {Ip}: dropped one message ({Bytes} bytes).",
                state.IpAddress, data.Length);
        }
    }

    /// <summary>
    /// The acknowledgement the monitor waits for: ACK^R01 with MSA|AA and the control id of the
    /// message, applications and facilities swapped. Null when the text has no MSH-10 to answer.
    /// </summary>
    internal static string? BuildAck(string message, DateTime now)
    {
        var controlId = NihonKohdenParser.Field(message, "MSH", 10);
        if (controlId.Length == 0) return null;

        var version = NihonKohdenParser.Field(message, "MSH", 12);
        return string.Join('|',
                   "MSH",
                   "^~\\&",
                   NihonKohdenParser.Field(message, "MSH", 5),
                   NihonKohdenParser.Field(message, "MSH", 6),
                   NihonKohdenParser.Field(message, "MSH", 3),
                   NihonKohdenParser.Field(message, "MSH", 4),
                   now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture),
                   "",
                   "ACK^R01",
                   controlId,
                   "P",
                   version.Length > 0 ? version : "2.4")
               + "\r"
               + $"MSA|AA|{controlId}\r";
    }

    /// <summary>True when the address is one of the configured monitors.</summary>
    public bool Owns(string monitorIp) => _options.Monitors.Any(m => m.Ip == monitorIp);

    public async Task<MonitorAdmissionResult> AssignPatientAsync(
        string monitorIp, string patientId, string patientName, CancellationToken cancellationToken = default)
    {
        if (Prepare(monitorIp, patientId, out var monitor, out var known) is { } refusal) return refusal;

        // Somebody without an id is on the monitor: name them and keep what the monitor has
        // recorded. Anybody else there is a different patient, so this is a new admission -
        // and so is a monitor with nobody admitted, which refuses the update.
        if (known.PatientId.Length == 0 && monitor!.PatientInfoPort > 0)
        {
            var updated = await SendAsync(monitor, monitor.PatientInfoPort, NihonKohdenAdt.Update, known.BedName,
                patientId, patientName, "update", cancellationToken);
            if (updated.Accepted) return Remember(monitorIp, known.BedName, patientId, updated);
        }

        if (known.PatientId == patientId) return new MonitorAdmissionResult(MonitorAdmissionStatus.Accepted, "already admitted");

        var admitted = await SendAsync(monitor!, monitor!.AdmitPort, NihonKohdenAdt.Admit, known.BedName,
            patientId, patientName, "admit", cancellationToken);
        return admitted.Accepted ? Remember(monitorIp, known.BedName, patientId, admitted) : admitted;
    }

    public async Task<MonitorAdmissionResult> UpdatePatientAsync(
        string monitorIp, string patientId, string patientName, CancellationToken cancellationToken = default)
    {
        if (Prepare(monitorIp, patientId, out var monitor, out var known) is { } refusal) return refusal;

        // The update writes PID-3 too, so sent to a monitor holding somebody else it would
        // put this patient's id on their data.
        if (known.PatientId != patientId)
        {
            return new MonitorAdmissionResult(MonitorAdmissionStatus.Rejected,
                $"the monitor reports patient \"{known.PatientId}\", not this one");
        }

        return await SendAsync(monitor!, monitor!.PatientInfoPort, NihonKohdenAdt.Update, known.BedName,
            patientId, patientName, "update", cancellationToken);
    }

    /// <summary>Everything that can be settled without talking to the monitor; null when a message may be sent.</summary>
    private MonitorAdmissionResult? Prepare(
        string monitorIp, string patientId, out NihonKohdenMonitor? monitor, out (string BedName, string PatientId) known)
    {
        known = default;
        monitor = _options.Monitors.FirstOrDefault(m => m.Ip == monitorIp);
        if (monitor is null || monitor.AdmitPort <= 0)
        {
            return new MonitorAdmissionResult(MonitorAdmissionStatus.NotSupported);
        }

        if (NihonKohdenAdt.WhyIdCannotBeStored(patientId) is { } reason)
        {
            return new MonitorAdmissionResult(MonitorAdmissionStatus.InvalidPatientId, reason);
        }

        lock (_sync)
        {
            if (!_reported.TryGetValue(monitorIp, out known))
            {
                return new MonitorAdmissionResult(MonitorAdmissionStatus.BedUnknown,
                    "the monitor has not sent a report with its bed name yet");
            }
        }

        return null;
    }

    private async Task<MonitorAdmissionResult> SendAsync(
        NihonKohdenMonitor monitor,
        int port,
        Func<string, string, string, string, DateTime, string> build,
        string bedName,
        string patientId,
        string patientName,
        string what,
        CancellationToken cancellationToken)
    {
        var controlId = "MMS" + Interlocked.Increment(ref _adtSequence).ToString(CultureInfo.InvariantCulture);
        var answer = await _adtExchange(monitor.Ip, port, build(bedName, patientId, patientName, controlId, DateTime.Now), cancellationToken);

        var result = answer is null
            ? new MonitorAdmissionResult(MonitorAdmissionStatus.Unreachable, $"no answer on port {port}")
            : NihonKohdenAdt.IsAccepted(answer)
                ? new MonitorAdmissionResult(MonitorAdmissionStatus.Accepted, what)
                : new MonitorAdmissionResult(MonitorAdmissionStatus.Rejected,
                    $"the monitor answered MSA|{NihonKohdenParser.Field(answer, "MSA", 1)} to the {what}");

        _logger.Log(result.Accepted ? LogLevel.Information : LogLevel.Debug,
            "Nihon Kohden monitor {Ip} (bed {Bed}): {What} of patient {PatientId} on port {Port}: {Status} {Detail}",
            monitor.Ip, bedName, what, patientId, port, result.Status, result.Detail);
        return result;
    }

    /// <summary>The monitor says so itself within a minute; until then this is what it was just told.</summary>
    private MonitorAdmissionResult Remember(string monitorIp, string bedName, string patientId, MonitorAdmissionResult result)
    {
        lock (_sync) { _reported[monitorIp] = (bedName, patientId); }
        return result;
    }

    private void MarkUp(DeviceState state)
    {
        lock (_sync)
        {
            state.State = DeviceStateEnum.Connected;
            if (!_connected.Contains(state)) _connected.Add(state);
        }

        _logger.LogInformation("Nihon Kohden monitor {Ip}:{Port} is connected.", state.IpAddress, state.Port);
        OnDeviceConnected?.Invoke(this, state);
    }

    private void MarkDown(DeviceState state)
    {
        lock (_sync)
        {
            if (!_connected.Remove(state)) return;
            state.State = DeviceStateEnum.Disconnected;
        }

        OnDeviceDisconnected?.Invoke(this, state);
    }

    public void Dispose()
    {
        try
        {
            _stopping.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
