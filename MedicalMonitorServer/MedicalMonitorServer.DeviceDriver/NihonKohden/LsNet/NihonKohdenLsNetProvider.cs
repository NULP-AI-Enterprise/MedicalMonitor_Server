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
/// <para>
/// Unless <see cref="LsNetOptions.ReadAlarmLimits"/> is turned off, a streaming monitor is also
/// asked for its alarm limits now and then (<see cref="LsNetProtocol.LimitsQuery"/>); a change
/// of them it reports by itself. Nothing else of what a central station asks its beds is
/// sent: one of those requests takes the waveforms out of the stream.
/// </para>
/// <para>
/// With <see cref="LsNetOptions.RemoteNibp"/> turned on, a station can also have a streaming
/// monitor start a cuff measurement or stop the one that is running (<see cref="RequestCuff"/>).
/// That is the one thing this driver changes on a monitor.
/// </para>
/// </summary>
public sealed class NihonKohdenLsNetProvider : IDeviceDataProvider, IRemoteCuff
{
    private static readonly TimeSpan SubscribeRetry = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StreamQuiet = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PatientQueryInterval = TimeSpan.FromSeconds(10);

    // A second start this soon after one is not sent: the monitor has not had the time to
    // report what it did with the first.
    private static readonly TimeSpan CuffStartSpacing = TimeSpan.FromSeconds(3);

    /// <summary>
    /// How long the monitor is given to show in its reports what it did with a cuff command
    /// before the log says that it did nothing. A start showed there after one to three
    /// seconds, a stop after two.
    /// </summary>
    internal TimeSpan CuffAnswerWait { get; set; } = TimeSpan.FromSeconds(8);

    // Reports in a row, one a second, that may come without any of the waveforms that were
    // asked for and that the monitor says it can stream, before they are asked for again.
    private const int ReportsWithoutWavesBeforeAsking = 3;

    // The alarm list is passed on when it changes, and this often regardless: a patient bound to
    // the monitor after the alarm started must get it too.
    private static readonly TimeSpan AlarmRefresh = TimeSpan.FromSeconds(10);

    // 072044: the same pulse rate as 044006 under a second code, alarming with it. One alert is enough.
    private const ushort DuplicatePulseRate = 0x482C;

    // Ids of the technical alarms on the station, clear of the parameter codes the limit alarms use.
    private const int TechnicalAlarmBase = 0x10000;

    private const double PressureCountsPerMmHg = 8;

    // A respiration trace has no unit; it is put within -0.5 .. 0.5. The monitor's own
    // simulation ran from -34 to 967 (2026-11-22): 4096 leaves that twice the room before a
    // sample is clipped. What a real chest gives has not been seen.
    private const double RespirationCountsFullScale = 4096;

    private static readonly TimeSpan WaveRangeEvery = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(100);

    private readonly CancellationTokenSource _stopping = new();
    private readonly LsNetOptions _options;
    private readonly Func<IPAddress, ILogger, ILsNetTransport> _transportFactory;
    private readonly ILogger _logger;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Dictionary<IPAddress, Bed> _beds = new();
    private readonly object _sync = new();

    // Cuff commands stations asked for, waiting for the run loop: it sends everything, so the
    // sequence numbers and the transport stay in one hand.
    private readonly System.Collections.Concurrent.ConcurrentQueue<(Bed Bed, bool Start)> _cuffRequests = new();
    private readonly IPAddress? _self;
    private readonly ushort[] _waveCodes;
    private ILsNetTransport? _transport;
    private ushort _sequence;
    private int _started;

    /// <summary>
    /// What is known about one monitor. Touched by the run loop only, <see cref="State"/> and
    /// <see cref="LastCuffStart"/> aside.
    /// </summary>
    private sealed class Bed
    {
        /// <summary>When a station last had a start accepted, in ticks of the clock; set from request threads.</summary>
        public long LastCuffStart = long.MinValue;

        /// <summary>The cuff command that was sent and whose effect has not shown in a report yet.</summary>
        public (bool Start, TimeSpan At, bool Acknowledged)? CuffAsked;

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
        public string TechnicalSignature = "";
        public TimeSpan LastTechnical = TimeSpan.MinValue;
        public string ParameterSignature = "";
        public string OfferedSignature = "";
        public Dictionary<ushort, LsNetNumeric> LastNumerics = new();
        public CuffClock Cuff = new();
        public int ReportsWithoutWaves;
        public TimeSpan LastLimitsQuery = TimeSpan.MinValue;
        public IReadOnlyList<LsNetAlarmLimit>? Limits;
        public string LimitsLogged = "";
        public string LimitsPassedOn = "";
        public ushort[] TemperatureCodes = Array.Empty<ushort>();
        public Dictionary<ushort, (short Min, short Max)> WaveRanges = new();
        public TimeSpan LastWaveRanges = TimeSpan.MinValue;
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
            "LS-NET: announcing this PC as {Kind} \"{BedName}\" from {LocalIp}; reading {Count} monitor(s), waveforms {Waves}{Limits}{Cuff}.",
            _options.AnnounceAsBed ? "bed" : "node", _options.BedName, _self, _beds.Count, string.Join(", ", _waveCodes.Select(c => c.ToString("X4", CultureInfo.InvariantCulture))),
            _options.ReadAlarmLimits ? ", and their alarm limits" : "",
            _options.RemoteNibp ? "; stations may start and stop a cuff measurement on them" : "");

        _ = RunAsync(_transport, _stopping.Token);
    }

    /// <summary>
    /// True for a monitor this driver reads, once <see cref="LsNetOptions.RemoteNibp"/> allows it
    /// and the driver is on the network. Says nothing of whether the monitor is streaming now.
    /// </summary>
    public bool CanControlCuff(string monitorIp) =>
        _options.RemoteNibp && _self is not null
        && IPAddress.TryParse(monitorIp, out var address) && _beds.ContainsKey(address);

    /// <summary>
    /// Has the monitor start a cuff measurement, or stop the one that is running. The command
    /// goes out from the run loop within a tenth of a second and is never repeated: what the
    /// monitor made of it is in its reports (<see cref="UmecVitalsMessage.NibpMeasuring"/>)
    /// and, one line each, in the log.
    /// <para>
    /// A start is refused while the monitor reports a measurement running, and for a few
    /// seconds after another start. A stop is always passed on: it is the safe direction.
    /// </para>
    /// </summary>
    public CuffRequestStatus RequestCuff(string monitorIp, bool start)
    {
        if (!IPAddress.TryParse(monitorIp, out var address) || !_beds.TryGetValue(address, out var bed))
        {
            return CuffRequestStatus.NotSupported;
        }

        if (!_options.RemoteNibp) return CuffRequestStatus.NotAllowed;

        bool up;
        lock (_sync) { up = bed.Up; }
        if (!up) return CuffRequestStatus.NotStreaming;

        if (start)
        {
            // What the last report said, a second old at most.
            if (bed.Cuff.Measuring) return CuffRequestStatus.AlreadyMeasuring;

            long now = _clock.Elapsed.Ticks;
            long last = Interlocked.Read(ref bed.LastCuffStart);
            if (last != long.MinValue && now - last < CuffStartSpacing.Ticks) return CuffRequestStatus.TooSoon;

            // Two stations at the same instant: one start goes out, the other came too soon.
            if (Interlocked.CompareExchange(ref bed.LastCuffStart, now, last) != last) return CuffRequestStatus.TooSoon;
        }

        _cuffRequests.Enqueue((bed, start));
        return CuffRequestStatus.Accepted;
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

                while (_cuffRequests.TryDequeue(out var request)) SendCuffControl(transport, request.Bed, request.Start);

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

        // Only a monitor that is streaming: one that is off or away is asked for its stream
        // alone, and for the rest when it is back.
        if (_options.ReadAlarmLimits && bed.Up
            && (bed.LastLimitsQuery == TimeSpan.MinValue || now - bed.LastLimitsQuery >= TimeSpan.FromMilliseconds(Math.Max(1000, _options.AlarmLimitsIntervalMs))))
        {
            transport.Send(LsNetProtocol.LimitsQuery(++_sequence, bed.NodeId, _self!), bed.Address);
            bed.LastLimitsQuery = now;
        }

        // The stream is there but empty of what was asked for (see OnStream): ask again. Not
        // more often than the subscription is repeated to a monitor that is silent. It may be
        // the first time of asking: a monitor goes on streaming to this address for some
        // seconds after a server that was reading it stopped, and the one started in its place
        // then finds a stream it never asked for.
        if (bed.Up && bed.ReportsWithoutWaves >= ReportsWithoutWavesBeforeAsking
            && (bed.LastSubscribe == TimeSpan.MinValue || now - bed.LastSubscribe >= SubscribeRetry))
        {
            _logger.LogWarning(
                "Nihon Kohden monitor {Ip} is streaming without the waveforms it offers; asking for them again.",
                bed.State.IpAddress);
            transport.Send(LsNetProtocol.Subscribe(++_sequence, bed.NodeId, _self!, _waveCodes), bed.Address);
            bed.LastSubscribe = now;
            bed.ReportsWithoutWaves = 0;
        }

        // A cuff command whose effect never showed. Said once, and left at that: a command
        // that inflates a cuff is not sent a second time on the server's own initiative.
        if (bed.CuffAsked is { } asked && now - asked.At > CuffAnswerWait)
        {
            _logger.LogWarning(
                "Nihon Kohden monitor {Ip} was told to {What} a cuff measurement {Seconds:0} s ago and its reports do not show it; " +
                "the monitor {Acknowledged} the command. It is not repeated.",
                bed.State.IpAddress, asked.Start ? "start" : "stop", (now - asked.At).TotalSeconds,
                asked.Acknowledged ? "acknowledged" : "did not acknowledge");
            bed.CuffAsked = null;
        }
    }

    /// <summary>
    /// Sends one cuff command a station asked for. From the run loop, and once: there is no
    /// retry behind it, whatever the monitor does.
    /// </summary>
    private void SendCuffControl(ILsNetTransport transport, Bed bed, bool start)
    {
        string what = start ? "start" : "stop";
        if (!bed.Up)
        {
            _logger.LogWarning(
                "Nihon Kohden monitor {Ip} stopped streaming before it could be told to {What} a cuff measurement; nothing was sent.",
                bed.State.IpAddress, what);
            return;
        }

        transport.Send(LsNetProtocol.CuffControl(start, ++_sequence, bed.NodeId, _self!), bed.Address);
        bed.CuffAsked = (start, _clock.Elapsed, false);
        _logger.LogInformation("Nihon Kohden monitor {Ip} was told to {What} a cuff measurement.", bed.State.IpAddress, what);
    }

    /// <summary>
    /// The monitor says where its cuff control stands - every second while a measurement is
    /// starting, whoever started it. Worth a line only as the answer to a command sent from
    /// here, and once.
    /// </summary>
    private void OnCuffPush(Bed bed, bool started)
    {
        if (bed.CuffAsked is not { } asked || asked.Acknowledged || asked.Start != started) return;

        bed.CuffAsked = (asked.Start, asked.At, true);
        _logger.LogInformation(
            "Nihon Kohden monitor {Ip} acknowledged the {What} of a cuff measurement after {Milliseconds:0} ms.",
            bed.State.IpAddress, started ? "start" : "stop", (_clock.Elapsed - asked.At).TotalMilliseconds);
    }

    private void Handle(ILsNetTransport transport, LsNetReceived received)
    {
        if (!_beds.TryGetValue(received.From, out var bed)) return;
        if (LsNetProtocol.Parse(received.Data) is not { } datagram) return;

        // Asked for (an answer) or sent after a change on the monitor (a push): the same table.
        if (datagram.AlarmLimits is { } limits) OnAlarmLimits(bed, limits);

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
                    bed.LastTechnical = TimeSpan.MinValue;
                }

                bed.PatientId = datagram.PatientId;
                bed.PatientKnown = true;
                break;

            case LsNetKind.Push when datagram.CuffStarted is { } started:
                OnCuffPush(bed, started);
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
        LogWaveRanges(bed, datagram.Waves);

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
            // A report rides behind the samples of its packet. One that comes without any,
            // from a monitor that says it can stream a waveform we asked for, is a stream that
            // has lost its selection: the monitor does that to a node that sends it a central
            // station's "monitor this bed" (seen 2026-11-27), and goes on sending the values.
            // Nothing puts the waveforms back but asking again, which Maintain then does.
            bool emptied = datagram.Kind == LsNetKind.Waveforms && datagram.Waves.Count == 0
                           && _waveCodes.Any(code => datagram.OfferedWaves.Contains(code));
            bed.ReportsWithoutWaves = emptied ? bed.ReportsWithoutWaves + 1 : 0;

            AddVitals(oru, numerics, CuffTakenAt(bed, numerics));

            // After CuffTakenAt, which is what watches the cuff. While this is set, the cuff
            // values in the report are the reading before, or none: a station shows that a
            // new one is on its way instead of a reading gone missing.
            oru.NibpMeasuring = bed.Cuff.Measuring;

            // The effect of a cuff command sent from here shows in a report, and only there.
            if (bed.CuffAsked is { } asked && bed.Cuff.Measuring == asked.Start)
            {
                _logger.LogInformation(
                    "Nihon Kohden monitor {Ip} reports {State} {Milliseconds:0} ms after it was told to {What}.",
                    bed.State.IpAddress,
                    asked.Start ? "a cuff measurement running" : "no cuff measurement running",
                    (_clock.Elapsed - asked.At).TotalMilliseconds,
                    asked.Start ? "start one" : "stop it");
                bed.CuffAsked = null;
            }

            LogParameters(bed, numerics);
            LogChanges(bed, datagram);

            // Which temperature is the first and which the second is the order of this list;
            // the limits of a temperature are passed on under the same number.
            bed.TemperatureCodes = numerics
                .Where(n => NihonKohdenCodes.IsTemperature(NihonKohdenCodes.FromBytes((byte)(n.Code >> 8), (byte)n.Code)))
                .Select(n => n.Code)
                .ToArray();
            PassOnAlarmLimits(bed);

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

        if (datagram.Numerics is not null)
        {
            PassOnAlarms(bed, datagram.Numerics);
            PassOnTechnicalAlarms(bed, datagram);
        }
    }

    /// <summary>
    /// The monitor's technical alarms - a sensor that is off, not a value out of limits. There is
    /// one for the monitor as a whole in the block header, and each parameter can carry its own.
    /// A state that is only a message ("measuring", "off") is not an alarm and is left out.
    /// </summary>
    private void PassOnTechnicalAlarms(Bed bed, LsNetDatagram datagram)
    {
        var oru = new UmecVitalsMessage();

        if (LsNetState.LevelOf(datagram.TechnicalState) != LsNetAlarm.None)
        {
            oru.Alarms.Add(TechnicalAlarm(
                TechnicalAlarmBase + datagram.TechnicalCode,
                datagram.TechnicalCode == LsNetState.CheckElectrodes
                    ? "Check electrodes"
                    : $"Monitor technical alarm ({datagram.TechnicalCode:X2})",
                datagram.TechnicalState));
        }

        // A sensor that is off sets the same alarm on every parameter it feeds - an arterial
        // line on its systolic, diastolic and mean. It is one alarm about the sensor, and is
        // passed on once, under the first of those parameters.
        var raised = new HashSet<(string Sensor, byte State, byte Message)>();
        foreach (var numeric in datagram.Numerics!)
        {
            if (LsNetState.LevelOf(numeric.State) == LsNetAlarm.None || numeric.Code == DuplicatePulseRate) continue;

            var sensor = NihonKohdenCodes.Sensor(NihonKohdenCodes.FromBytes((byte)(numeric.Code >> 8), (byte)numeric.Code));
            if (!raised.Add((sensor, numeric.State, numeric.StateCode))) continue;

            oru.Alarms.Add(TechnicalAlarm(
                2 * TechnicalAlarmBase + numeric.Code,
                NihonKohdenCodes.TechnicalAlarmText(sensor, numeric.StateCode),
                numeric.State));
        }

        var signature = string.Join(",", oru.Alarms.Select(a => $"{a.AlarmId:X}:{a.LevelCode:X2}"));
        var now = _clock.Elapsed;
        if (signature == bed.TechnicalSignature && bed.LastTechnical != TimeSpan.MinValue && now - bed.LastTechnical < AlarmRefresh) return;

        if (signature != bed.TechnicalSignature)
        {
            _logger.LogInformation("Nihon Kohden monitor {Ip}: technical alarms now [{Alarms}].", bed.State.IpAddress,
                string.Join(", ", oru.Alarms.Select(a => $"{a.Text} ({a.Level})")));
        }

        bed.TechnicalSignature = signature;
        bed.LastTechnical = now;

        OnMessageReceived?.Invoke(new UmecMessage
        {
            Kind = UmecMessageKind.TechnicalAlarms,
            MessageType = "LS-NET",
            ReceivedAt = DateTime.Now,
            SourceIp = bed.State.IpAddress,
            Oru = oru,
        });
    }

    private static ActiveAlarm TechnicalAlarm(int id, string text, byte state) => new()
    {
        Category = AlarmCategory.Technical,
        TypeCode = 3,
        AlarmId = id,
        Text = text,
        LevelCode = state,
        Level = LevelOf(LsNetState.LevelOf(state)),
        RaisedAt = DateTime.Now,
    };

    private static AlarmLevel LevelOf(byte alarm) => alarm switch
    {
        LsNetAlarm.Crisis => AlarmLevel.High,
        LsNetAlarm.Warning => AlarmLevel.Medium,
        LsNetAlarm.Advisory => AlarmLevel.Low,
        _ => AlarmLevel.Unknown,        // not one of the monitor's three levels
    };

    /// <summary>
    /// The monitor's limit alarms, as their own message: the feed takes a list of alarms as all
    /// that are active on the monitor, so it is the whole list every time, empty when there is none.
    /// </summary>
    private void PassOnAlarms(Bed bed, IReadOnlyList<LsNetNumeric> numerics)
    {
        var oru = new UmecVitalsMessage();
        int temperatures = 0;
        foreach (var numeric in numerics)
        {
            var code = NihonKohdenCodes.FromBytes((byte)(numeric.Code >> 8), (byte)numeric.Code);

            // For every record, in alarm or not: it is what numbers the temperatures.
            int? parameterId = TryTarget(code, ref temperatures, out var target) ? target.ParameterId : null;

            if (numeric.Alarm == LsNetAlarm.None || numeric.Code == DuplicatePulseRate) continue;

            oru.Alarms.Add(new ActiveAlarm
            {
                Category = AlarmCategory.Physiological,
                TypeCode = 1,
                AlarmId = numeric.Code,
                // The alarm is on the record of a value: the station marks that value with it.
                ParameterId = parameterId,
                // The monitor says the value has crossed one of its limits, not which one.
                Text = $"{NihonKohdenCodes.Label(code)} out of limits",
                LevelCode = numeric.Alarm,
                Level = LevelOf(numeric.Alarm),
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
        bool unitless = false;
        switch (wave.Code)
        {
            case 0x0100: type = WaveformType.ECG_I; step = 1 / _options.EcgCountsPerMillivolt; break;
            case 0x0101: type = WaveformType.ECG_II; step = 1 / _options.EcgCountsPerMillivolt; break;
            case 0x0700: type = WaveformType.Pleth; step = 1 / 8192.0; unitless = true; break;
            case 0x0500: type = WaveformType.Respiration; step = 1 / RespirationCountsFullScale; unitless = true; break;

            // A pressure sample is an eighth of a mmHg: while the monitor read ART 118/76 with a
            // mean of 91 its trace ran from 606 to 942 around 730 (capture 04 of 2026-11-21).
            // The venous trace is taken to be on the same scale, see the check in LogWaveRanges.
            case 0x2C00: type = WaveformType.ArterialPressure; step = 1 / PressureCountsPerMmHg; break;
            case 0x3500: type = WaveformType.CentralVenousPressure; step = 1 / PressureCountsPerMmHg; break;
            default: return null;
        }

        var samples = new float[wave.Samples.Length];
        for (int i = 0; i < samples.Length; i++)
        {
            double value = wave.Samples[i] * step;
            if (unitless) value = Math.Clamp(value, -0.5, 0.5);
            samples[i] = (float)Math.Round(value, 4);
        }

        return new ScaledWave { Type = type, Samples = samples, SampleRate = LsNetProtocol.SamplesPerSecond };
    }

    /// <summary>
    /// One line whenever the monitor's list of parameters changes: every code it sends with
    /// the value it has right now, and which of them are not passed on. The wire carries no
    /// names, so this line next to the monitor's screen is how a new code gets identified.
    /// </summary>
    private void LogParameters(Bed bed, IReadOnlyList<LsNetNumeric> numerics)
    {
        var signature = string.Join(",", numerics.Select(n => n.Code.ToString("X4", CultureInfo.InvariantCulture)));
        if (signature == bed.ParameterSignature) return;
        bed.ParameterSignature = signature;

        static string Code(LsNetNumeric n) => NihonKohdenCodes.FromBytes((byte)(n.Code >> 8), (byte)n.Code);
        static string Entry(LsNetNumeric n) => $"{Code(n)}={n.Value?.ToString(CultureInfo.InvariantCulture) ?? "--"}";

        var unread = numerics
            .Where(n => !NihonKohdenCodes.IsTemperature(Code(n)) && !NihonKohdenCodes.TryGetParameter(Code(n), out _))
            .Select(Entry);

        _logger.LogInformation(
            "Nihon Kohden monitor {Ip} reports parameters [{Parameters}]; not passed on: [{Unread}].",
            bed.State.IpAddress, string.Join(", ", numerics.Select(Entry)), string.Join(", ", unread));
    }

    /// <summary>
    /// The trail for working out what a code means: at Debug, every numeric whose value or
    /// status bytes changed since the last report, old and new, and the list of waveforms the
    /// monitor offers when that changes. Read next to the monitor's screen while one thing at
    /// a time is changed on it. Off unless this class logs at Debug.
    /// </summary>
    private void LogChanges(Bed bed, LsNetDatagram datagram)
    {
        var offered = string.Join(" ", datagram.OfferedWaves.Select(c => c.ToString("X4", CultureInfo.InvariantCulture)));
        if (offered != bed.OfferedSignature)
        {
            bed.OfferedSignature = offered;
            _logger.LogInformation("Nihon Kohden monitor {Ip} offers waveforms [{Waves}].", bed.State.IpAddress, offered);
        }

        if (!_logger.IsEnabled(LogLevel.Debug))
        {
            bed.LastNumerics.Clear();
            return;
        }

        static string Show(LsNetNumeric n) =>
            $"{n.Value?.ToString(CultureInfo.InvariantCulture) ?? "--"} [{n.RawStatus:X12}]";

        var changes = new List<string>();
        var now = new Dictionary<ushort, LsNetNumeric>();
        foreach (var numeric in datagram.Numerics!)
        {
            now[numeric.Code] = numeric;
            var code = NihonKohdenCodes.FromBytes((byte)(numeric.Code >> 8), (byte)numeric.Code);

            if (!bed.LastNumerics.TryGetValue(numeric.Code, out var before))
            {
                changes.Add($"{code} appears {Show(numeric)}");
            }
            else if (before != numeric)
            {
                changes.Add($"{code} {Show(before)} -> {Show(numeric)}");
            }
        }

        foreach (var gone in bed.LastNumerics.Keys.Where(code => !now.ContainsKey(code)))
        {
            changes.Add($"{NihonKohdenCodes.FromBytes((byte)(gone >> 8), (byte)gone)} gone");
        }

        bed.LastNumerics = now;
        if (changes.Count > 0)
        {
            _logger.LogDebug("Nihon Kohden monitor {Ip} changed: {Changes}", bed.State.IpAddress, string.Join("; ", changes));
        }
    }

    /// <summary>
    /// At Debug, every five seconds: the lowest and highest raw sample of each waveform in
    /// that time. This is how a scale is found or checked - a pressure trace must run between
    /// eight times the diastolic and eight times the systolic the monitor reports alongside.
    /// </summary>
    private void LogWaveRanges(Bed bed, IReadOnlyList<LsNetWave> waves)
    {
        if (!_logger.IsEnabled(LogLevel.Debug) || waves.Count == 0) return;

        foreach (var wave in waves)
        {
            if (wave.Samples.Length == 0) continue;

            short low = wave.Samples.Min(), high = wave.Samples.Max();
            bed.WaveRanges[wave.Code] = bed.WaveRanges.TryGetValue(wave.Code, out var range)
                ? (Math.Min(range.Min, low), Math.Max(range.Max, high))
                : (low, high);
        }

        var now = _clock.Elapsed;
        if (bed.LastWaveRanges == TimeSpan.MinValue) bed.LastWaveRanges = now;
        if (now - bed.LastWaveRanges < WaveRangeEvery) return;

        _logger.LogDebug("Nihon Kohden monitor {Ip} raw waveform ranges: {Ranges}", bed.State.IpAddress,
            string.Join(", ", bed.WaveRanges.OrderBy(r => r.Key).Select(r => $"{r.Key:X4} {r.Value.Min}..{r.Value.Max}")));

        bed.WaveRanges.Clear();
        bed.LastWaveRanges = now;
    }

    /// <summary>When the cuff reading in this report was taken, see <see cref="CuffClock"/>.</summary>
    private static DateTime? CuffTakenAt(Bed bed, IReadOnlyList<LsNetNumeric> numerics)
    {
        // 009000 systolic, 009001 diastolic, 009002 mean. While a measurement runs 009008 is 1
        // and the three carry state 45 with a message code that moves between 1E, 18 and 14
        // (watched on 2026-11-22), so no single message code can stand for "measuring". A
        // measurement that fails takes the values off before it ends, so one that ends with
        // values still there has produced them.
        int? Value(ushort code) => numerics.FirstOrDefault(n => n.Code == code).Value;
        bool measuring = Value(0x0908) == 1
            || numerics.Any(n => n.Code is 0x0900 or 0x0901 or 0x0902 && n.State == 0x45);

        return bed.Cuff.Observe(Value(0x0900), Value(0x0901), Value(0x0902), measuring, DateTime.Now);
    }

    private static void AddVitals(UmecVitalsMessage oru, IReadOnlyList<LsNetNumeric> numerics, DateTime? cuffTakenAt = null)
    {
        int temperatures = 0;
        foreach (var numeric in numerics)
        {
            var code = NihonKohdenCodes.FromBytes((byte)(numeric.Code >> 8), (byte)numeric.Code);
            if (!TryTarget(code, ref temperatures, out var target)) continue;

            double? value = numeric.Value is int raw ? Math.Round(raw * NihonKohdenCodes.LsNetScale(code), 2) : null;
            var vital = new VitalSign
            {
                ParameterId = target.ParameterId,
                Name = target.Name,
                Label = code,
                Value = value,
                RawValue = numeric.Value?.ToString(CultureInfo.InvariantCulture) ?? "",
                IsAperiodic = NihonKohdenCodes.IsAperiodic(code),
                MeasuredAt = NihonKohdenCodes.IsAperiodic(code) ? cuffTakenAt : null,
            };

            // Two codes can stand for one measurement (the respiration rate): it is listed
            // once, by the code that has a value.
            int listed = oru.Vitals.FindIndex(v => v.ParameterId == vital.ParameterId);
            if (listed < 0)
            {
                oru.Vitals.Add(vital);
            }
            else if (oru.Vitals[listed].Value is null && value is not null)
            {
                oru.Vitals[listed] = vital;
            }
        }
    }

    /// <summary>
    /// The parameter id a value of this code is read under, false for one that is not passed
    /// on. The monitor has a code per temperature site, so the temperatures of a report are
    /// numbered in the order they come: <paramref name="temperatures"/> counts them, and has
    /// to see every record of the report in turn.
    /// </summary>
    private static bool TryTarget(string code, ref int temperatures, out (int ParameterId, string Name) target)
    {
        if (!NihonKohdenCodes.IsTemperature(code)) return NihonKohdenCodes.TryGetParameter(code, out target);

        if (temperatures >= NihonKohdenCodes.Temperatures.Length)
        {
            target = default;
            return false;
        }

        target = NihonKohdenCodes.Temperatures[temperatures++];
        return true;
    }

    /// <summary>
    /// The monitor's table of alarm limits has come: kept, and its limits written to the log
    /// when they are not the ones logged before. The table names every parameter the monitor
    /// could measure; only those with a limit switched on are worth a line.
    /// </summary>
    private void OnAlarmLimits(Bed bed, IReadOnlyList<LsNetAlarmLimit> limits)
    {
        bed.Limits = limits;

        static string Code(LsNetAlarmLimit l) => NihonKohdenCodes.FromBytes((byte)(l.Code >> 8), (byte)l.Code);
        static string Show(LsNetAlarmLimit l)
        {
            var code = Code(l);
            double scale = NihonKohdenCodes.LsNetScale(code);
            string At(int? raw) => raw is int value ? (value * scale).ToString("0.##", CultureInfo.InvariantCulture) : "";

            // Known parameters by the monitor's name for them, the rest by their code.
            return $"{(NihonKohdenCodes.IsNamed(code) ? NihonKohdenCodes.Label(code) : code)} {At(l.Lower)}..{At(l.Upper)}";
        }

        var set = limits.Where(l => l.Upper is not null || l.Lower is not null).ToList();
        var logged = string.Join(", ", set.Select(Show));
        if (logged != bed.LimitsLogged)
        {
            bed.LimitsLogged = logged;
            _logger.LogInformation(
                "Nihon Kohden monitor {Ip} alarm limits, lower..upper: [{Limits}]; {Off} more parameters have none set.",
                bed.State.IpAddress, logged, limits.Count - set.Count);
        }

        PassOnAlarmLimits(bed);
    }

    /// <summary>
    /// Hands the limits of the values that are passed on to the feed, as the alarm settings of
    /// their parameters, when they differ from what was handed over last. A limit is in the
    /// units of its value. Called for every table and every report: which temperature is the
    /// first is only known from a report.
    /// </summary>
    private void PassOnAlarmLimits(Bed bed)
    {
        if (bed.Limits is not { } limits) return;

        var oru = new UmecVitalsMessage();
        foreach (var limit in limits)
        {
            var code = NihonKohdenCodes.FromBytes((byte)(limit.Code >> 8), (byte)limit.Code);

            (int ParameterId, string Name) target;
            if (NihonKohdenCodes.IsTemperature(code))
            {
                int site = Array.IndexOf(bed.TemperatureCodes, limit.Code);
                if (site < 0 || site >= NihonKohdenCodes.Temperatures.Length) continue;
                target = NihonKohdenCodes.Temperatures[site];
            }
            else if (!NihonKohdenCodes.TryGetParameter(code, out target))
            {
                continue;
            }

            // Two codes can be read under one id (see AddVitals): one setting each, the first.
            if (oru.AlarmSettings.Any(s => s.ParameterId == target.ParameterId)) continue;

            double scale = NihonKohdenCodes.LsNetScale(code);
            oru.AlarmSettings.Add(new AlarmSetting
            {
                ParameterId = target.ParameterId,
                ParameterName = target.Name,
                HighLimit = limit.Upper is int upper ? Math.Round(upper * scale, 2) : null,
                LowLimit = limit.Lower is int lower ? Math.Round(lower * scale, 2) : null,
            });
        }

        var signature = string.Join(",", oru.AlarmSettings.Select(s =>
            string.Create(CultureInfo.InvariantCulture, $"{s.ParameterId}:{s.LowLimit}:{s.HighLimit}")));
        if (signature == bed.LimitsPassedOn) return;
        bed.LimitsPassedOn = signature;

        OnMessageReceived?.Invoke(new UmecMessage
        {
            Kind = UmecMessageKind.AlarmLimits,
            MessageType = "LS-NET",
            ReceivedAt = DateTime.Now,
            SourceIp = bed.State.IpAddress,
            Oru = oru,
        });
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
        bed.LastLimitsQuery = TimeSpan.MinValue;
        bed.ReportsWithoutWaves = 0;
        bed.Limits = null;
        bed.LimitsLogged = "";
        bed.LimitsPassedOn = "";
        bed.LastAlarms = TimeSpan.MinValue;
        bed.LastTechnical = TimeSpan.MinValue;
        bed.ParameterSignature = "";
        bed.OfferedSignature = "";
        bed.LastNumerics.Clear();
        bed.Cuff.Reset();
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
        bed.CuffAsked = null;
        _logger.LogWarning("Nihon Kohden monitor {Ip} stopped streaming over LS-NET ({Reason}); asking again.", bed.State.IpAddress, reason);
        OnDeviceDisconnected?.Invoke(this, bed.State);
    }

    /// <summary>
    /// "0101", "0700" -> codes; every waveform a station draws when the list is empty or holds
    /// nothing usable: ECG II, arterial pressure, pleth, central venous pressure, respiration.
    /// </summary>
    private static ushort[] WaveCodes(IEnumerable<string> configured)
    {
        var codes = configured
            .Select(text => ushort.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code) ? code : (ushort)0)
            .Where(code => code != 0)
            .Distinct()
            .ToArray();
        return codes.Length > 0 ? codes : new ushort[] { 0x0101, 0x2C00, 0x0700, 0x3500, 0x0500 };
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
