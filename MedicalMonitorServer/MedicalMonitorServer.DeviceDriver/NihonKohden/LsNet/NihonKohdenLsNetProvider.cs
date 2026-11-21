using System.Diagnostics;
using System.Globalization;
using System.Net;
using MedicalMonitorServer.Contracts.Api;
using MedicalMonitorServer.Contracts.Models;
using MedicalMonitorServer.DeviceDriver.Umec;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MedicalMonitorServer.DeviceDriver.NihonKohden.LsNet;

/// <summary>
/// Reads Nihon Kohden monitors that are set to LS-NET: this PC announces itself on the monitor
/// network as one more bed, asks every configured monitor for its waveforms, and passes on
/// what they stream - four packets of samples a second, the numeric values once a second.
/// See <see cref="LsNetProtocol"/> for the wire format and where it comes from.
/// <para>
/// A subscription lasts as long as the monitor keeps hearing our beacon; when its stream
/// goes quiet all the same (monitor restarted, cable out) the request is simply repeated.
/// </para>
/// </summary>
public sealed class NihonKohdenLsNetProvider : IDeviceDataProvider
{
    private static readonly TimeSpan SubscribeRetry = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StreamQuiet = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PatientQueryInterval = TimeSpan.FromSeconds(10);

    // The alarm list is passed on when it changes, and this often regardless: a patient bound to
    // the monitor after the alarm started must get it too.
    private static readonly TimeSpan AlarmRefresh = TimeSpan.FromSeconds(10);

    // 072044: the same pulse rate as 044006 under a second code, alarming with it. One alert is enough.
    private const ushort DuplicatePulseRate = 0x482C;
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(100);

    private readonly CancellationTokenSource _stopping = new();
    private readonly LsNetOptions _options;
    private readonly Func<IPAddress, ILogger, ILsNetTransport> _transportFactory;
    private readonly ILogger _logger;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Dictionary<IPAddress, Bed> _beds = new();
    private readonly object _sync = new();
    private readonly IPAddress? _self;
    private readonly ushort[] _waveCodes;
    private ILsNetTransport? _transport;
    private ushort _sequence;
    private int _started;

    /// <summary>What is known about one monitor. Touched by the run loop only, <see cref="State"/> aside.</summary>
    private sealed class Bed
    {
        public required DeviceState State { get; init; }
        public required IPAddress Address { get; init; }
        public required byte[] NodeId { get; set; }
        public string BedName = "";
        public string PatientId = "";
        public bool PatientKnown;
        public bool Up;
        public TimeSpan LastStream = TimeSpan.MinValue;
        public TimeSpan LastSubscribe = TimeSpan.MinValue;
        public TimeSpan LastPatientQuery = TimeSpan.MinValue;
        public int LastSequence = -1;
        public long Lost;
        public HashSet<ushort> UnknownWaves = new();
        public string AlarmSignature = "";
        public TimeSpan LastAlarms = TimeSpan.MinValue;
    }

    /// <param name="monitors">The monitors to read; those not set to LS-NET are ignored.</param>
    /// <param name="transportFactory">Opens the UDP side for our address; tests substitute it.</param>
    public NihonKohdenLsNetProvider(
        LsNetOptions options,
        IEnumerable<NihonKohdenMonitor> monitors,
        ILogger<NihonKohdenLsNetProvider>? logger = null,
        Func<IPAddress, ILogger, ILsNetTransport>? transportFactory = null)
    {
        _options = options;
        _logger = logger ?? NullLogger<NihonKohdenLsNetProvider>.Instance;
        _transportFactory = transportFactory ?? ((address, log) => new LsNetUdpTransport(address, log));
        _waveCodes = WaveCodes(options.Waveforms);

        if (!IPAddress.TryParse(options.LocalIp, out _self) || _self.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            _self = null;
        }

        foreach (var monitor in monitors.Where(m => m.IsLsNet))
        {
            if (!IPAddress.TryParse(monitor.Ip, out var address))
            {
                _logger.LogError("Nihon Kohden monitor \"{Name}\" has no usable address ({Ip}); skipped.", monitor.Name, monitor.Ip);
                continue;
            }

            _beds[address] = new Bed
            {
                Address = address,
                NodeId = LsNetProtocol.NodeId(address),
                State = new DeviceState
                {
                    Name = monitor.Name.Length > 0 ? monitor.Name : $"Nihon Kohden {monitor.Ip}",
                    IpAddress = monitor.Ip,
                    Port = LsNetProtocol.StreamPort.ToString(CultureInfo.InvariantCulture),
                    State = DeviceStateEnum.Disconnected,
                },
            };
        }
    }

    public event Action<IDeviceDataProvider, DeviceState>? OnDeviceConnected;

    public event Action<IDeviceDataProvider, DeviceState>? OnDeviceDisconnected;

    public event Action<UmecMessage>? OnMessageReceived;

    public IReadOnlyCollection<DeviceState> ConnectedDevices
    {
        get { lock (_sync) { return _beds.Values.Where(b => b.Up).Select(b => b.State).ToArray(); } }
    }

    public IReadOnlyCollection<PatientInfo> Patients => Array.Empty<PatientInfo>();

    /// <summary>Starts announcing and subscribing. Subscribe to the events first.</summary>
    public void Start()
    {
        if (_beds.Count == 0 || Interlocked.Exchange(ref _started, 1) != 0) return;

        if (_self is null)
        {
            _logger.LogError(
                "NihonKohden:LsNet:LocalIp is \"{LocalIp}\"; it must be this PC's IPv4 address on the monitor network. " +
                "LS-NET monitors are not read.", _options.LocalIp);
            return;
        }

        try
        {
            _transport = _transportFactory(_self, _logger);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "LS-NET could not open its UDP ports on {LocalIp} ({Control}, {Stream}, {Source}); is the address on this PC and are the ports free?",
                _self, LsNetProtocol.ControlPort, LsNetProtocol.StreamPort, LsNetProtocol.ControlSourcePort);
            return;
        }

        _logger.LogInformation(
            "LS-NET: announcing this PC as {Kind} \"{BedName}\" from {LocalIp}; reading {Count} monitor(s), waveforms {Waves}.",
            _options.AnnounceAsBed ? "bed" : "node", _options.BedName, _self, _beds.Count, string.Join(", ", _waveCodes.Select(c => c.ToString("X4", CultureInfo.InvariantCulture))));

        _ = RunAsync(_transport, _stopping.Token);
    }

    /// <summary>Nothing to do: the watchdog's finds are uMec monitors, ours come from configuration.</summary>
    public void Connect(DeviceState deviceState)
    {
    }

    public void Disconnect(DeviceState deviceState)
    {
    }

    private async Task RunAsync(ILsNetTransport transport, CancellationToken cancellationToken)
    {
        var beacon = LsNetProtocol.Beacon(_options.BedName, _self!, _options.AnnounceAsBed);
        var beaconEvery = TimeSpan.FromMilliseconds(_options.BeaconIntervalMs);
        var nextBeacon = TimeSpan.Zero;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                while (transport.Incoming.TryRead(out var received))
                {
                    try
                    {
                        Handle(transport, received);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "LS-NET: dropped one datagram from {From} ({Bytes} bytes).", received.From, received.Data.Length);
                    }
                }

                var now = _clock.Elapsed;
                if (now >= nextBeacon)
                {
                    transport.Broadcast(beacon);
                    nextBeacon = now + beaconEvery;
                }

                foreach (var bed in _beds.Values) Maintain(transport, bed, now);

                await Task.WhenAny(transport.Incoming.WaitToReadAsync(cancellationToken).AsTask(), Task.Delay(Tick, cancellationToken));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LS-NET stopped; Nihon Kohden monitors are no longer read.");
        }
        finally
        {
            // Tell the monitors we are gone, as the BSM-3562 does when its own Interbed window closes.
            foreach (var bed in _beds.Values.Where(b => b.Up))
            {
                transport.Send(LsNetProtocol.Unsubscribe(++_sequence, bed.NodeId, _self!), bed.Address);
                MarkDown(bed, "the server is stopping");
            }
        }
    }

    /// <summary>Keeps one monitor's stream coming and notices when it stops.</summary>
    private void Maintain(ILsNetTransport transport, Bed bed, TimeSpan now)
    {
        var quiet = bed.LastStream == TimeSpan.MinValue ? TimeSpan.MaxValue : now - bed.LastStream;

        if (bed.Up && quiet > TimeSpan.FromMilliseconds(_options.StreamTimeoutMs))
        {
            MarkDown(bed, $"nothing for {quiet.TotalSeconds:0} s");
        }

        if (quiet > StreamQuiet && (bed.LastSubscribe == TimeSpan.MinValue || now - bed.LastSubscribe >= SubscribeRetry))
        {
            transport.Send(LsNetProtocol.Subscribe(++_sequence, bed.NodeId, _self!, _waveCodes), bed.Address);
            bed.LastSubscribe = now;
        }

        if (bed.Up && (bed.LastPatientQuery == TimeSpan.MinValue || now - bed.LastPatientQuery >= PatientQueryInterval))
        {
            transport.Send(LsNetProtocol.PatientQuery(++_sequence, bed.NodeId, _self!), bed.Address);
            bed.LastPatientQuery = now;
        }
    }

    private void Handle(ILsNetTransport transport, LsNetReceived received)
    {
        if (!_beds.TryGetValue(received.From, out var bed)) return;
        if (LsNetProtocol.Parse(received.Data) is not { } datagram) return;

        switch (datagram.Kind)
        {
            case LsNetKind.Beacon:
                // The id a request must carry and the bed name, straight from the monitor.
                bed.NodeId = datagram.NodeId;
                bed.BedName = datagram.BedName;
                break;

            case LsNetKind.Notification:
                transport.Send(LsNetProtocol.BareAcknowledgement, bed.Address);
                break;

            case LsNetKind.Response when datagram.Type == LsNetProtocol.PatientType + 1:
                if (!bed.PatientKnown || bed.PatientId != datagram.PatientId)
                {
                    // Whoever the next report is routed to has not been told the alarms yet.
                    bed.LastAlarms = TimeSpan.MinValue;
                }

                bed.PatientId = datagram.PatientId;
                bed.PatientKnown = true;
                break;

            case LsNetKind.Waveforms:
            case LsNetKind.Numerics:
                OnStream(bed, datagram);
                break;
        }
    }

    private void OnStream(Bed bed, LsNetDatagram datagram)
    {
        bed.LastStream = _clock.Elapsed;
        if (!bed.Up) MarkUp(bed);

        if (datagram.Kind == LsNetKind.Waveforms)
        {
            if (bed.LastSequence >= 0)
            {
                int missed = ((datagram.Sequence - bed.LastSequence) & 0xFFFF) - 1;
                if (missed is > 0 and < 1000)
                {
                    bed.Lost += missed;
                    _logger.LogWarning("Nihon Kohden monitor {Ip}: {Missed} waveform packet(s) lost ({Total} since connecting).",
                        bed.State.IpAddress, missed, bed.Lost);
                }
            }

            bed.LastSequence = datagram.Sequence;
        }

        var oru = new UmecVitalsMessage();

        foreach (var wave in datagram.Waves)
        {
            if (Scale(wave) is { } scaled)
            {
                oru.ScaledWaves.Add(scaled);
            }
            else if (bed.UnknownWaves.Add(wave.Code))
            {
                _logger.LogInformation("Nihon Kohden monitor {Ip} sends waveform {Code:X4}, which stations have no trace for.",
                    bed.State.IpAddress, wave.Code);
            }
        }

        if (datagram.Numerics is { } numerics)
        {
            AddVitals(oru, numerics);

            // The block is the monitor's whole list: a sensor that came off is either gone from
            // it or there without a value, and the station must stop showing its last number.
            oru.VitalsAreComplete = true;

            // With the values, so the roster learns who is on the monitor as often as it learns the values.
            oru.Patient = new PatientInfo
            {
                PatientId = bed.PatientId,
                // Known and empty is "the monitor has no patient id"; not asked yet is "no word on it".
                PidPresent = bed.PatientKnown,
                BedLabel = bed.BedName,
                BedNumber = TrailingNumber(bed.BedName),
                MonitorIp = bed.State.IpAddress,
            };
        }

        if (oru.ScaledWaves.Count == 0 && datagram.Numerics is null) return;

        OnMessageReceived?.Invoke(new UmecMessage
        {
            Kind = datagram.Numerics is null ? UmecMessageKind.Waveforms : UmecMessageKind.Vitals,
            MessageType = "LS-NET",
            ControlId = datagram.Sequence.ToString(CultureInfo.InvariantCulture),
            ReceivedAt = DateTime.Now,
            SourceIp = bed.State.IpAddress,
            Oru = oru,
        });

        if (datagram.Numerics is not null) PassOnAlarms(bed, datagram.Numerics);
    }

    /// <summary>
    /// The monitor's limit alarms, as their own message: the feed takes a list of alarms as all
    /// that are active on the monitor, so it is the whole list every time, empty when there is none.
    /// </summary>
    private void PassOnAlarms(Bed bed, IReadOnlyList<LsNetNumeric> numerics)
    {
        var oru = new UmecVitalsMessage();
        foreach (var numeric in numerics)
        {
            if (numeric.Alarm == LsNetAlarm.None || numeric.Code == DuplicatePulseRate) continue;

            var code = NihonKohdenCodes.FromBytes((byte)(numeric.Code >> 8), (byte)numeric.Code);
            oru.Alarms.Add(new ActiveAlarm
            {
                Category = AlarmCategory.Physiological,
                TypeCode = 1,
                AlarmId = numeric.Code,
                Text = $"{NihonKohdenCodes.Label(code)} limit alarm",
                LevelCode = numeric.Alarm,
                Level = numeric.Alarm switch
                {
                    LsNetAlarm.Crisis => AlarmLevel.High,
                    LsNetAlarm.Warning => AlarmLevel.Medium,
                    LsNetAlarm.Advisory => AlarmLevel.Low,
                    _ => AlarmLevel.Unknown,        // not one of the monitor's three levels
                },
                RaisedAt = DateTime.Now,
            });
        }

        var signature = string.Join(",", oru.Alarms.Select(a => $"{a.AlarmId:X4}:{a.LevelCode:X2}"));
        var now = _clock.Elapsed;
        if (signature == bed.AlarmSignature && bed.LastAlarms != TimeSpan.MinValue && now - bed.LastAlarms < AlarmRefresh) return;

        if (signature != bed.AlarmSignature)
        {
            _logger.LogInformation("Nihon Kohden monitor {Ip}: limit alarms now [{Alarms}].", bed.State.IpAddress,
                string.Join(", ", oru.Alarms.Select(a => $"{a.Text} ({a.Level})")));
        }

        bed.AlarmSignature = signature;
        bed.LastAlarms = now;

        OnMessageReceived?.Invoke(new UmecMessage
        {
            Kind = UmecMessageKind.PhysiologicalAlarms,
            MessageType = "LS-NET",
            ReceivedAt = DateTime.Now,
            SourceIp = bed.State.IpAddress,
            Oru = oru,
        });
    }

    /// <summary>
    /// A waveform in the units stations draw, null for one they have no trace for. The ECG
    /// scale is configuration, because it has not been measured; a pleth has no unit, so it is
    /// put within -0.5 .. 0.5 by the 14 bits its samples were seen to stay inside.
    /// </summary>
    private ScaledWave? Scale(LsNetWave wave)
    {
        WaveformType type;
        double step;
        switch (wave.Code)
        {
            case 0x0100: type = WaveformType.ECG_I; step = 1 / _options.EcgCountsPerMillivolt; break;
            case 0x0101: type = WaveformType.ECG_II; step = 1 / _options.EcgCountsPerMillivolt; break;
            case 0x0700: type = WaveformType.Pleth; step = 1 / 8192.0; break;
            default: return null;
        }

        var samples = new float[wave.Samples.Length];
        for (int i = 0; i < samples.Length; i++)
        {
            double value = wave.Samples[i] * step;
            if (type == WaveformType.Pleth) value = Math.Clamp(value, -0.5, 0.5);
            samples[i] = (float)Math.Round(value, 4);
        }

        return new ScaledWave { Type = type, Samples = samples, SampleRate = LsNetProtocol.SamplesPerSecond };
    }

    private static void AddVitals(UmecVitalsMessage oru, IReadOnlyList<LsNetNumeric> numerics)
    {
        int temperatures = 0;
        foreach (var numeric in numerics)
        {
            var code = NihonKohdenCodes.FromBytes((byte)(numeric.Code >> 8), (byte)numeric.Code);

            (int ParameterId, string Name) target;
            if (NihonKohdenCodes.IsTemperature(code))
            {
                if (temperatures >= NihonKohdenCodes.Temperatures.Length) continue;
                target = NihonKohdenCodes.Temperatures[temperatures++];
            }
            else if (!NihonKohdenCodes.TryGetParameter(code, out target))
            {
                continue;
            }

            double? value = numeric.Value is int raw ? Math.Round(raw * NihonKohdenCodes.LsNetScale(code), 2) : null;
            oru.Vitals.Add(new VitalSign
            {
                ParameterId = target.ParameterId,
                Name = target.Name,
                Label = code,
                Value = value,
                RawValue = numeric.Value?.ToString(CultureInfo.InvariantCulture) ?? "",
                IsAperiodic = NihonKohdenCodes.IsAperiodic(code),
            });
        }
    }

    private void MarkUp(Bed bed)
    {
        lock (_sync)
        {
            bed.Up = true;
            bed.State.State = DeviceStateEnum.Connected;
        }

        bed.LastSequence = -1;
        bed.LastPatientQuery = TimeSpan.MinValue;
        bed.LastAlarms = TimeSpan.MinValue;
        _logger.LogInformation("Nihon Kohden monitor {Ip} is streaming over LS-NET.", bed.State.IpAddress);
        OnDeviceConnected?.Invoke(this, bed.State);
    }

    private void MarkDown(Bed bed, string reason)
    {
        lock (_sync)
        {
            bed.Up = false;
            bed.State.State = DeviceStateEnum.Disconnected;
        }

        bed.PatientKnown = false;
        _logger.LogWarning("Nihon Kohden monitor {Ip} stopped streaming over LS-NET ({Reason}); asking again.", bed.State.IpAddress, reason);
        OnDeviceDisconnected?.Invoke(this, bed.State);
    }

    /// <summary>"0101", "0700" -> codes; ECG II and pleth when the list is empty or holds nothing usable.</summary>
    private static ushort[] WaveCodes(IEnumerable<string> configured)
    {
        var codes = configured
            .Select(text => ushort.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code) ? code : (ushort)0)
            .Where(code => code != 0)
            .Distinct()
            .ToArray();
        return codes.Length > 0 ? codes : new ushort[] { 0x0101, 0x0700 };
    }

    /// <summary>"ICU-001" -> "1": the roster numbers beds, the monitor names them.</summary>
    private static string TrailingNumber(string bedName)
    {
        int start = bedName.Length;
        while (start > 0 && char.IsAsciiDigit(bedName[start - 1])) start--;
        return bedName[start..].TrimStart('0');
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

        // Give the run loop a moment to send its goodbyes before the sockets go.
        var transport = _transport;
        if (transport is not null) _ = Task.Delay(300).ContinueWith(_ => transport.Dispose());
    }
}
