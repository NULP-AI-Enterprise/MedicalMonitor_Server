using System.Net;
using System.Threading.Channels;
using MedicalMonitorServer.Contracts.Api;
using MedicalMonitorServer.Contracts.Models;
using MedicalMonitorServer.DeviceDriver.NihonKohden;
using MedicalMonitorServer.DeviceDriver.NihonKohden.LsNet;

namespace MedicalMonitorServer.DeviceDriver.Tests;

/// <summary>
/// The LS-NET driver against datagrams of a real BSM-3562 (SW 07-41) recorded on 2026-11-21
/// with the monitor in simulation mode: HR 80, SpO2 98, ART 118/76, 36.5 and 37.0 °C on its
/// screen. The "-sent" entries of the fixture are requests the monitor accepted, so building
/// the same bytes is what keeps the driver on the protocol as it was observed.
/// </summary>
public class NihonKohdenLsNetTests
{
    private static readonly IPAddress Self = IPAddress.Parse("10.200.200.77");
    private static readonly IPAddress Monitor = IPAddress.Parse("10.10.46.48");
    private static readonly byte[] MonitorId = { 0x0A, 0x2E, 0x30, 0x20 };

    private static readonly Dictionary<string, byte[]> Wire = File
        .ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "nihon_kohden_lsnet_bsm3562.txt"))
        .Where(line => line.Length > 0 && line[0] != '#')
        .Select(line => line.Split(' ', 2))
        .ToDictionary(parts => parts[0], parts => Convert.FromHexString(parts[1].Trim()));

    private static LsNetDatagram Parsed(string name) => LsNetProtocol.Parse(Wire[name])!;

    // ------------------------------------------------------------ what we send

    [Fact]
    public void Requests_are_the_bytes_the_monitor_accepted()
    {
        Assert.Equal(Wire["subscribe-1-sent"], LsNetProtocol.Subscribe(0x0201, MonitorId, Self, new ushort[] { 0x0101 }));
        Assert.Equal(Wire["subscribe-3-sent"], LsNetProtocol.Subscribe(0x0201, MonitorId, Self, new ushort[] { 0x0101, 0x0700, 0x2C00 }));
        Assert.Equal(Wire["unsubscribe-sent"], LsNetProtocol.Unsubscribe(0x0202, MonitorId, Self));
    }

    [Fact]
    public void Our_beacon_is_the_monitors_own_with_our_address_and_name()
    {
        var ours = LsNetProtocol.Beacon("ICU-001", Monitor, asBed: true);

        Assert.Equal(Wire["beacon"], ours);
        Assert.Equal(MonitorId, LsNetProtocol.NodeId(Monitor));
    }

    [Fact]
    public void By_default_we_announce_a_node_the_monitor_does_not_list_as_a_bed()
    {
        var asBed = LsNetProtocol.Beacon("STATION", Self, asBed: true);
        var asNode = LsNetProtocol.Beacon("STATION", Self);

        // One byte apart: the last of the node id, 0x20 for a bedside monitor.
        var differing = Enumerable.Range(0, 64).Where(i => asBed[i] != asNode[i]).ToArray();
        Assert.Equal(new[] { 55 }, differing);
        Assert.Equal((0x20, 0x00), (asBed[55], asNode[55]));
    }

    [Fact]
    public void A_bed_name_longer_than_the_field_is_cut()
    {
        var beacon = LsNetProtocol.Parse(LsNetProtocol.Beacon("NURSE-STATION-7", Self))!;

        Assert.Equal("NURSE-STATI", beacon.BedName);
    }

    // ------------------------------------------------------------ what the monitor sends

    [Fact]
    public void Beacon_names_the_bed_and_the_node()
    {
        var beacon = Parsed("beacon");

        Assert.Equal(LsNetKind.Beacon, beacon.Kind);
        Assert.Equal("ICU-001", beacon.BedName);
        Assert.Equal(MonitorId, beacon.NodeId);
    }

    [Fact]
    public void A_response_carries_the_type_and_sequence_of_its_request()
    {
        var response = Parsed("subscribe-response");

        Assert.Equal(LsNetKind.Response, response.Kind);
        Assert.Equal(LsNetProtocol.SubscribeType + 1, response.Type);
        Assert.Equal(0x0201, response.Sequence);
    }

    [Fact]
    public void Patient_response_holds_the_id()
    {
        var response = Parsed("patient-response");

        Assert.Equal(LsNetProtocol.PatientType + 1, response.Type);
        Assert.Equal("P-0017", response.PatientId);
    }

    [Fact]
    public void A_waveform_packet_is_32_samples_of_each_requested_trace()
    {
        var packet = Parsed("wave1-1");

        Assert.Equal(LsNetKind.Waveforms, packet.Kind);
        Assert.Equal(0x1315, packet.Sequence);
        Assert.Null(packet.Numerics);
        var ecg = Assert.Single(packet.Waves);
        Assert.Equal(0x0101, ecg.Code);
        Assert.Equal(32, ecg.Samples.Length);
    }

    [Fact]
    public void Three_traces_come_in_one_packet()
    {
        var packet = Parsed("wave3-0");

        Assert.Equal(new ushort[] { 0x0101, 0x0700, 0x2C00 }, packet.Waves.Select(w => w.Code));
        Assert.All(packet.Waves, w => Assert.Equal(32, w.Samples.Length));
        Assert.Equal(new short[] { -35, -760, 730 }, packet.Waves.Select(w => w.Samples[0]));
    }

    [Fact]
    public void Once_a_second_the_values_ride_behind_the_samples()
    {
        var numerics = Parsed("wave1-0").Numerics!.ToDictionary(n => n.Code, n => n.Value);

        Assert.Equal(34, numerics.Count);
        Assert.Equal(80, numerics[0x0100]);         // HR
        Assert.Equal(5, numerics[0x0301]);          // ST II, hundredths of a mV
        Assert.Equal(-3, numerics[0x0303]);         // ST aVR
        Assert.Equal(98, numerics[0x0700]);         // SpO2
        Assert.Equal(365, numerics[0x0B00]);        // skin temperature, tenths
        Assert.Equal(118, numerics[0x2C00]);        // ART systolic
        Assert.Null(numerics[0x0500]);              // respiration is switched off on the monitor: 0x8000
    }

    [Fact]
    public void Values_alone_have_the_same_block()
    {
        var packet = Parsed("numerics-only");

        Assert.Equal(LsNetKind.Numerics, packet.Kind);
        Assert.Empty(packet.Waves);
        Assert.Equal(80, packet.Numerics!.Single(n => n.Code == 0x0100).Value);
    }

    [Fact]
    public void A_notification_is_told_apart_so_it_can_be_acknowledged()
    {
        Assert.Equal(LsNetKind.Notification, Parsed("notification").Kind);
        Assert.Equal(12, LsNetProtocol.BareAcknowledgement.Length);
    }

    [Fact]
    public void A_cut_or_padded_datagram_is_not_decoded()
    {
        var wave = Wire["wave1-1"];

        Assert.Null(LsNetProtocol.Parse(wave.AsSpan(0, wave.Length - 1)));
        Assert.Null(LsNetProtocol.Parse(wave.Concat(new byte[] { 0 }).ToArray()));
        Assert.Null(LsNetProtocol.Parse(new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public void A_sample_format_never_seen_is_not_drawn()
    {
        var wave = (byte[])Wire["wave1-1"].Clone();
        wave[38] = 0x10;                            // the byte that is 08 in every block captured

        Assert.Null(LsNetProtocol.Parse(wave));
    }

    [Fact]
    public void A_numeric_block_that_does_not_end_with_the_datagram_is_left_out()
    {
        var wave = (byte[])Wire["wave1-0"].Clone();
        int countAt = wave.Length - 34 * 10 - 2;
        wave[countAt] = 33;                         // one record fewer than there is room for

        var packet = LsNetProtocol.Parse(wave)!;

        Assert.Single(packet.Waves);
        Assert.Null(packet.Numerics);
    }

    // ------------------------------------------------------------ the monitor's alarms

    private static LsNetNumeric Record(string packet, ushort code) => Parsed(packet).Numerics!.Single(n => n.Code == code);

    [Fact]
    public void A_value_within_its_limits_carries_no_alarm()
    {
        Assert.All(Parsed("alarm-none").Numerics!, n => Assert.Equal(LsNetAlarm.None, n.Alarm));
    }

    [Fact]
    public void A_limit_alarm_is_on_the_record_of_the_value_with_its_level()
    {
        // The same SpO2 alarm, before and after its priority was changed on the monitor.
        Assert.Equal(LsNetAlarm.Warning, Record("alarm-warning-spo2", 0x0700).Alarm);
        Assert.Equal(LsNetAlarm.Crisis, Record("alarm-crisis-spo2", 0x0700).Alarm);

        // Nothing else alarms with it.
        Assert.Single(Parsed("alarm-crisis-spo2").Numerics!, n => n.Alarm != LsNetAlarm.None);

        // The third priority, raised on the skin temperature.
        Assert.Equal(LsNetAlarm.Advisory, Record("alarm-advisory-temp", 0x0B00).Alarm);
    }

    [Fact]
    public void Silence_Alarms_on_the_monitor_stops_the_sound_not_the_alarm()
    {
        var sounding = Record("alarm-warning-pr", 0x2C06);
        var silenced = Record("alarm-silenced-pr", 0x2C06);

        Assert.Equal((LsNetAlarm.Warning, true), (sounding.Alarm, sounding.Sounding));
        Assert.Equal((LsNetAlarm.Warning, false), (silenced.Alarm, silenced.Sounding));
    }

    [Fact]
    public void An_electrode_that_is_off_is_a_technical_alarm_of_the_monitor_and_no_heart_rate()
    {
        var off = Parsed("technical-electrodes-off");

        Assert.Equal((0x24, LsNetState.CheckElectrodes), (off.TechnicalState, off.TechnicalCode));
        Assert.Equal(LsNetAlarm.Advisory, LsNetState.LevelOf(off.TechnicalState));      // blue on the monitor
        Assert.Null(off.Numerics!.Single(n => n.Code == 0x0100).Value);                 // "---", not zero

        Assert.Equal(LsNetAlarm.None, LsNetState.LevelOf(Parsed("technical-none").TechnicalState));
    }

    [Fact]
    public void A_message_that_is_not_an_alarm_has_no_level()
    {
        // The monitor's own note after the electrode went back on, and "measuring" on the cuff values.
        var note = Parsed("technical-message-only");
        Assert.Equal(0x45, note.TechnicalState);
        Assert.Equal(LsNetAlarm.None, LsNetState.LevelOf(note.TechnicalState));

        var cuff = Parsed("technical-nibp-measuring").Numerics!.Single(n => n.Code == 0x0900);
        Assert.Equal((0x45, 0x1E), (cuff.State, cuff.StateCode));
        Assert.Equal(LsNetAlarm.None, LsNetState.LevelOf(cuff.State));
    }

    // ------------------------------------------------------------ the provider

    private sealed class FakeTransport : ILsNetTransport
    {
        private readonly Channel<LsNetReceived> _incoming = Channel.CreateUnbounded<LsNetReceived>();
        private readonly List<(byte[] Data, IPAddress To)> _sent = new();
        private int _broadcasts;

        public ChannelReader<LsNetReceived> Incoming => _incoming.Reader;
        public bool Disposed { get; private set; }
        public int Broadcasts => Volatile.Read(ref _broadcasts);
        public byte[]? LastBroadcast { get; private set; }

        public IReadOnlyList<(byte[] Data, IPAddress To)> Sent
        {
            get { lock (_sent) { return _sent.ToArray(); } }
        }

        public int Count(ushort type, bool selectionEmpty = false) =>
            Sent.Count(s => s.Data.Length >= 32 && s.Data[0] == 0 && ((s.Data[12] << 8) | s.Data[13]) == type
                            && (type != LsNetProtocol.SubscribeType || (s.Data.Length == 48) == selectionEmpty));

        public void Send(byte[] datagram, IPAddress to) { lock (_sent) { _sent.Add((datagram, to)); } }
        public void Broadcast(byte[] datagram) { LastBroadcast = datagram; Interlocked.Increment(ref _broadcasts); }
        public void Deliver(string name, IPAddress? from = null, int port = LsNetProtocol.StreamPort) =>
            _incoming.Writer.TryWrite(new LsNetReceived(from ?? Monitor, port, Wire[name]));
        public void Dispose() => Disposed = true;
    }

    private static NihonKohdenLsNetProvider Provider(FakeTransport transport, Action<LsNetOptions>? configure = null, string localIp = "10.200.200.77")
    {
        var options = new LsNetOptions { LocalIp = localIp, BedName = "STATION", BeaconIntervalMs = 20, StreamTimeoutMs = 300 };
        configure?.Invoke(options);
        return new NihonKohdenLsNetProvider(
            options,
            new[]
            {
                new NihonKohdenMonitor { Name = "BSM-3562", Ip = "10.10.46.48", Protocol = "LsNet" },
                new NihonKohdenMonitor { Name = "HL7 one", Ip = "10.10.46.49", Protocol = "HL7" },
            },
            transportFactory: (_, _) => transport);
    }

    private static List<UmecMessage> Collect(NihonKohdenLsNetProvider provider)
    {
        var messages = new List<UmecMessage>();
        provider.OnMessageReceived += m => { lock (messages) { messages.Add(m); } };
        return messages;
    }

    [Fact]
    public async Task It_announces_itself_and_asks_the_LsNet_monitor_for_its_waveforms()
    {
        var transport = new FakeTransport();
        using var provider = Provider(transport);

        provider.Start();

        await Wait.UntilAsync(() => transport.Broadcasts >= 2 && transport.Count(LsNetProtocol.SubscribeType) >= 1, "a beacon and a request go out");
        Assert.Equal(LsNetProtocol.Beacon("STATION", Self), transport.LastBroadcast);

        var request = transport.Sent.First(s => s.Data[12] == 0x02 && s.Data[13] == 0x22);
        Assert.Equal(Monitor, request.To);
        // ECG II, arterial pressure, pleth, central venous pressure, respiration: all a station draws.
        Assert.Equal(
            LsNetProtocol.Subscribe(1, MonitorId, Self, new ushort[] { 0x0101, 0x2C00, 0x0700, 0x3500, 0x0500 }).AsSpan(4).ToArray(),
            request.Data.AsSpan(4).ToArray());
        Assert.DoesNotContain(transport.Sent, s => s.To.ToString() == "10.10.46.49");   // that one is on HL7
    }

    [Fact]
    public async Task Values_reach_the_feed_with_the_ids_and_units_it_reads()
    {
        var transport = new FakeTransport();
        using var provider = Provider(transport);
        var messages = Collect(provider);
        provider.Start();

        transport.Deliver("beacon", port: LsNetProtocol.ControlPort);
        transport.Deliver("wave1-0");

        await Wait.UntilAsync(() => Reports(messages).Count == 1, "the packet is passed on");
        var message = Reports(messages)[0];

        Assert.Equal(UmecMessageKind.Vitals, message.Kind);
        Assert.Equal("10.10.46.48", message.SourceIp);
        Assert.True(message.Oru!.VitalsAreComplete);      // the block is the monitor's whole list

        var vitals = message.Oru!.Vitals.ToDictionary(v => v.ParameterId, v => v.Value);
        Assert.Equal(80, vitals[101]);              // HR
        Assert.Equal(0.05, vitals[106]);            // ST-II
        Assert.Equal(-0.03, vitals[108]);           // ST-aVR
        Assert.Equal(98, vitals[160]);              // SpO2
        Assert.Equal(80, vitals[161]);              // PR
        Assert.Equal(36.5, vitals[200]);            // first temperature
        Assert.Equal(37.0, vitals[201]);            // second temperature
        Assert.Equal(118, vitals[ServerParameterIds.ArtSystolic]);      // the arterial line
        Assert.Equal(76, vitals[ServerParameterIds.ArtDiastolic]);
        Assert.Equal(91, vitals[ServerParameterIds.ArtMean]);
        Assert.Equal(8, vitals[ServerParameterIds.CvpMean]);            // central venous pressure
        Assert.Equal(0.08, vitals[ServerParameterIds.StV1]);            // ST of chest lead V1
        Assert.Equal(2.78, vitals[162]);            // perfusion index: 278 on the wire
        Assert.Null(vitals[151]);                   // respiration is switched off: listed once, without a value
        Assert.DoesNotContain(message.Oru.Vitals, v => v.Label == "072044");    // a second pulse rate nobody reads

        Assert.Equal(("ICU-001", "1", "10.10.46.48"),
            (message.Oru.Patient!.BedLabel, message.Oru.Patient.BedNumber, message.Oru.Patient.MonitorIp));
    }

    [Fact]
    public async Task Samples_arrive_scaled_for_the_station()
    {
        var transport = new FakeTransport();
        using var provider = Provider(transport, o => o.EcgCountsPerMillivolt = 400);
        var messages = Collect(provider);
        provider.Start();

        transport.Deliver("wave3-1");

        await Wait.UntilAsync(() => { lock (messages) { return messages.Count == 1; } }, "the packet is passed on");
        UmecMessage message;
        lock (messages) { message = messages[0]; }

        Assert.Equal(UmecMessageKind.Waveforms, message.Kind);
        Assert.Empty(message.Oru!.Vitals);
        Assert.False(message.Oru.VitalsAreComplete);       // samples alone say nothing about the values
        Assert.Null(message.Oru.Patient);

        // ECG II, pleth and the arterial pressure trace, in the order the monitor sends them.
        Assert.Equal(
            new[] { WaveformType.ECG_II, WaveformType.Pleth, WaveformType.ArterialPressure },
            message.Oru.ScaledWaves.Select(w => w.Type));
        Assert.All(message.Oru.ScaledWaves, w => Assert.Equal((125, 32), (w.SampleRate, w.Samples.Length)));

        var raw = Parsed("wave3-1").Waves;
        Assert.Equal((float)Math.Round(raw[0].Samples[5] / 400.0, 4), message.Oru.ScaledWaves[0].Samples[5]);
        Assert.All(message.Oru.ScaledWaves[1].Samples, s => Assert.InRange(s, -0.5f, 0.5f));

        // A pressure arrives in mmHg, an eighth of a mmHg a count: the monitor read 118/76 then.
        Assert.Equal((float)Math.Round(raw[2].Samples[5] / 8.0, 4), message.Oru.ScaledWaves[2].Samples[5]);
        Assert.All(message.Oru.ScaledWaves[2].Samples, s => Assert.InRange(s, 70f, 125f));
    }

    /// <summary>The reports with values in them; each also brings a list of alarms, a message of its own.</summary>
    private static List<UmecMessage> Reports(List<UmecMessage> messages)
    {
        lock (messages) { return messages.Where(m => m.Kind == UmecMessageKind.Vitals).ToList(); }
    }

    private static List<UmecMessage> Alarms(List<UmecMessage> messages)
    {
        lock (messages) { return messages.Where(m => m.Kind == UmecMessageKind.PhysiologicalAlarms).ToList(); }
    }

    [Fact]
    public async Task The_monitors_alarms_reach_the_feed_as_the_whole_list_with_their_level()
    {
        var transport = new FakeTransport();
        using var provider = Provider(transport);
        var messages = Collect(provider);
        provider.Start();

        transport.Deliver("alarm-warning-pr");
        await Wait.UntilAsync(() => Alarms(messages).Count == 1, "the alarm is passed on");

        // 044006 and its twin 072044 alarm together; the station gets one alert.
        var alarm = Assert.Single(Alarms(messages)[0].Oru!.Alarms);
        Assert.Equal((0x2C06, "PR (ART) out of limits", AlarmLevel.Medium, AlarmCategory.Physiological),
            (alarm.AlarmId, alarm.Text, alarm.Level, alarm.Category));
        Assert.Equal("10.10.46.48", Alarms(messages)[0].SourceIp);

        transport.Deliver("alarm-crisis-spo2");
        await Wait.UntilAsync(() => Alarms(messages).Count == 2, "the changed list is passed on");
        alarm = Assert.Single(Alarms(messages)[1].Oru!.Alarms);
        Assert.Equal((0x0700, "SpO2 out of limits", AlarmLevel.High), (alarm.AlarmId, alarm.Text, alarm.Level));
    }

    [Fact]
    public async Task The_three_priorities_of_the_monitor_become_high_medium_and_low()
    {
        var transport = new FakeTransport();
        using var provider = Provider(transport);
        var messages = Collect(provider);
        provider.Start();

        transport.Deliver("alarm-crisis-spo2");
        await Wait.UntilAsync(() => Alarms(messages).Count == 1, "the crisis alarm is passed on");
        transport.Deliver("alarm-warning-spo2");
        await Wait.UntilAsync(() => Alarms(messages).Count == 2, "the warning alarm is passed on");
        transport.Deliver("alarm-advisory-temp");
        await Wait.UntilAsync(() => Alarms(messages).Count == 3, "the advisory alarm is passed on");

        Assert.Equal(
            new[] { ("SpO2 out of limits", AlarmLevel.High), ("SpO2 out of limits", AlarmLevel.Medium), ("Skin temperature out of limits", AlarmLevel.Low) },
            Alarms(messages).Select(m => m.Oru!.Alarms.Single()).Select(a => (a.Text, a.Level)));
    }

    private static List<UmecMessage> Technical(List<UmecMessage> messages)
    {
        lock (messages) { return messages.Where(m => m.Kind == UmecMessageKind.TechnicalAlarms).ToList(); }
    }

    [Fact]
    public async Task Check_electrodes_reaches_the_feed_as_a_technical_alarm_and_goes_when_they_are_back()
    {
        var transport = new FakeTransport();
        using var provider = Provider(transport);
        var messages = Collect(provider);
        provider.Start();

        transport.Deliver("technical-electrodes-off");
        await Wait.UntilAsync(() => Technical(messages).Count == 1, "the technical alarm is passed on");

        var alarm = Assert.Single(Technical(messages)[0].Oru!.Alarms);
        Assert.Equal(("Check electrodes", AlarmCategory.Technical, AlarmLevel.Low), (alarm.Text, alarm.Category, alarm.Level));

        // The heart rate went with the signal: the report lists it without a value.
        Assert.DoesNotContain(Reports(messages)[0].Oru!.Vitals, v => v.ParameterId == 101 && v.Value is not null);

        // Electrode back on: first the monitor's note, which is no alarm, then nothing.
        transport.Deliver("technical-message-only");
        await Wait.UntilAsync(() => Technical(messages).Count == 2, "the end of the alarm is passed on");
        Assert.Empty(Technical(messages)[1].Oru!.Alarms);
    }

    [Fact]
    public async Task A_cuff_measurement_in_progress_is_not_an_alarm()
    {
        var transport = new FakeTransport();
        using var provider = Provider(transport);
        var messages = Collect(provider);
        provider.Start();

        transport.Deliver("technical-nibp-measuring");
        await Wait.UntilAsync(() => Technical(messages).Count == 1, "the technical list is passed on");

        Assert.Empty(Technical(messages)[0].Oru!.Alarms);
        Assert.Empty(Alarms(messages)[0].Oru!.Alarms);
    }

    [Fact]
    public async Task When_the_alarm_ends_the_feed_is_told_with_an_empty_list()
    {
        var transport = new FakeTransport();
        using var provider = Provider(transport);
        var messages = Collect(provider);
        provider.Start();

        transport.Deliver("alarm-warning-spo2");
        await Wait.UntilAsync(() => Alarms(messages).Count == 1, "the alarm is passed on");

        transport.Deliver("alarm-none");
        await Wait.UntilAsync(() => Alarms(messages).Count == 2, "its end is passed on");

        Assert.Empty(Alarms(messages)[1].Oru!.Alarms);
    }

    [Fact]
    public async Task An_unchanged_list_is_not_repeated_every_second()
    {
        var transport = new FakeTransport();
        using var provider = Provider(transport);
        var messages = Collect(provider);
        provider.Start();

        for (int i = 0; i < 5; i++) transport.Deliver("alarm-warning-spo2");

        await Wait.UntilAsync(() => Reports(messages).Count == 5, "all five reports are passed on");
        Assert.Single(Alarms(messages));
    }

    [Fact]
    public async Task The_patient_id_the_monitor_reports_goes_with_the_values()
    {
        var transport = new FakeTransport();
        using var provider = Provider(transport);
        var messages = Collect(provider);
        provider.Start();

        transport.Deliver("wave1-0");
        await Wait.UntilAsync(() => Reports(messages).Count == 1, "the first values are in");
        Assert.False(Reports(messages)[0].Oru!.Patient!.PidPresent);     // nobody has been asked yet

        await Wait.UntilAsync(() => transport.Count(LsNetProtocol.PatientType) >= 1, "the monitor is asked who its patient is");
        transport.Deliver("patient-response", port: LsNetProtocol.ControlPort);
        transport.Deliver("wave1-4");

        await Wait.UntilAsync(() => Reports(messages).Count == 2, "the next values are in");
        var second = Reports(messages)[1].Oru!.Patient!;
        Assert.True(second.PidPresent);
        Assert.Equal("P-0017", second.PatientId);
    }

    [Fact]
    public async Task The_monitor_is_listed_while_it_streams_and_asked_again_when_it_stops()
    {
        var transport = new FakeTransport();
        using var provider = Provider(transport);
        var events = new List<string>();
        provider.OnDeviceConnected += (_, s) => { lock (events) { events.Add("up " + s.IpAddress); } };
        provider.OnDeviceDisconnected += (_, s) => { lock (events) { events.Add("down " + s.IpAddress); } };
        provider.Start();

        transport.Deliver("wave1-1");
        await Wait.UntilAsync(() => provider.ConnectedDevices.Count == 1, "the monitor is reported connected");
        Assert.Equal(("BSM-3562", DeviceStateEnum.Connected), (provider.ConnectedDevices.Single().Name, provider.ConnectedDevices.Single().State));
        int asked = transport.Count(LsNetProtocol.SubscribeType);

        // No more packets: past the timeout it is down, and the request goes out again.
        await Wait.UntilAsync(() => provider.ConnectedDevices.Count == 0, "the silence is noticed");
        await Wait.UntilAsync(() => transport.Count(LsNetProtocol.SubscribeType) > asked, "the request is repeated", timeoutMs: 5000);
        lock (events) { Assert.Equal(new[] { "up 10.10.46.48", "down 10.10.46.48" }, events); }
    }

    [Fact]
    public async Task A_notification_gets_the_bare_acknowledgement()
    {
        var transport = new FakeTransport();
        using var provider = Provider(transport);
        provider.Start();

        transport.Deliver("notification", port: LsNetProtocol.ControlPort);

        await Wait.UntilAsync(() => transport.Sent.Any(s => s.Data.SequenceEqual(LsNetProtocol.BareAcknowledgement)), "it is acknowledged");
    }

    [Fact]
    public async Task Datagrams_from_other_addresses_are_ignored()
    {
        var transport = new FakeTransport();
        using var provider = Provider(transport);
        var messages = Collect(provider);
        provider.Start();

        transport.Deliver("wave1-0", from: IPAddress.Parse("10.10.46.99"));
        transport.Deliver("wave1-1");

        await Wait.UntilAsync(() => { lock (messages) { return messages.Count >= 1; } }, "the configured monitor's packet is passed on");
        lock (messages) { Assert.Single(messages); }
        Assert.Single(provider.ConnectedDevices);
    }

    [Fact]
    public async Task Stopping_tells_a_streaming_monitor_to_stop()
    {
        var transport = new FakeTransport();
        var provider = Provider(transport);
        provider.Start();
        transport.Deliver("wave1-1");
        await Wait.UntilAsync(() => provider.ConnectedDevices.Count == 1, "the monitor is streaming");

        provider.Dispose();

        await Wait.UntilAsync(() => transport.Count(LsNetProtocol.SubscribeType, selectionEmpty: true) == 1, "the empty selection is sent");
        await Wait.UntilAsync(() => transport.Disposed, "the sockets are closed");
    }

    [Fact]
    public void Without_this_PCs_address_nothing_is_opened()
    {
        bool opened = false;
        using var provider = new NihonKohdenLsNetProvider(
            new LsNetOptions { LocalIp = "" },
            new[] { new NihonKohdenMonitor { Ip = "10.10.46.48", Protocol = "LsNet" } },
            transportFactory: (_, _) => { opened = true; return new FakeTransport(); });

        provider.Start();

        Assert.False(opened);
        Assert.Empty(provider.ConnectedDevices);
    }
}
