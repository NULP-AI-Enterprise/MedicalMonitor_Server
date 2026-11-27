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
    public void The_request_for_the_alarm_limits_is_the_bytes_the_monitor_answered()
    {
        Assert.Equal(Wire["limits-request-sent"], LsNetProtocol.LimitsQuery(0x0705, MonitorId, Self));
    }

    [Fact]
    public void The_start_of_a_cuff_measurement_is_the_bytes_the_monitor_took()
    {
        var start = LsNetProtocol.CuffControl(start: true, 0x070B, MonitorId, Self);

        Assert.Equal(Wire["cuff-start-sent"], start);

        // Stopping is the same record with the value 0: one byte apart.
        var stop = LsNetProtocol.CuffControl(start: false, 0x070B, MonitorId, Self);
        var differing = Enumerable.Range(0, start.Length).Where(i => start[i] != stop[i]).ToArray();
        Assert.Equal(new[] { 38 }, differing);
        Assert.Equal((0x01, 0x00), (start[38], stop[38]));
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
    public void The_monitor_answers_a_cuff_command_by_pushing_the_record_back()
    {
        var push = Parsed("cuff-started-push");

        Assert.Equal((LsNetKind.Push, LsNetProtocol.RemoteParameterType), (push.Kind, push.Type));
        Assert.True(push.CuffStarted);

        // The same record with the value 0 says the cuff was stopped.
        Assert.False(LsNetProtocol.Parse(LsNetProtocol.CuffControl(start: false, 1, MonitorId, Monitor))!.CuffStarted);

        // A 0403 about another parameter, and a push of another type, say nothing about the cuff.
        var other = (byte[])Wire["cuff-started-push"].Clone();
        other[35] = 0x04;
        Assert.Null(LsNetProtocol.Parse(other)!.CuffStarted);
        Assert.Null(Parsed("limits-push").CuffStarted);
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
    public void A_stream_emptied_of_its_waveforms_still_comes_packet_by_packet()
    {
        // What the monitor went on sending after a central station's "monitor this bed" (0206).
        var empty = Parsed("stream-emptied");
        Assert.Equal(LsNetKind.Waveforms, empty.Kind);
        Assert.Equal(0x41A3, empty.Sequence);
        Assert.Empty(empty.Waves);
        Assert.Null(empty.Numerics);

        // The values still ride along once a second, and say which waveforms could be had.
        var values = Parsed("stream-emptied-values");
        Assert.Equal(0x41A4, values.Sequence);
        Assert.Empty(values.Waves);
        Assert.Equal(80, values.Numerics!.Single(n => n.Code == 0x0100).Value);
        Assert.Contains((ushort)0x0101, values.OfferedWaves);
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

    // ------------------------------------------------------------ the monitor's alarm limits

    private static Dictionary<ushort, LsNetAlarmLimit> LimitTable(string name) =>
        Parsed(name).AlarmLimits!.ToDictionary(l => l.Code);

    [Fact]
    public void The_answer_to_the_limits_request_is_the_monitors_table_of_limits()
    {
        var answer = Parsed("limits-response");
        Assert.Equal(LsNetKind.Response, answer.Kind);
        Assert.Equal(LsNetProtocol.LimitsType + 1, answer.Type);

        var limits = LimitTable("limits-response");
        Assert.Equal(100, limits.Count);                                            // every parameter the monitor could measure
        Assert.Equal(new LsNetAlarmLimit(0x0100, 140, 50), limits[0x0100]);         // heart rate
        Assert.Equal(new LsNetAlarmLimit(0x0700, null, 90), limits[0x0700]);        // SpO2: the upper limit is off
        Assert.Equal(new LsNetAlarmLimit(0x0900, 180, 80), limits[0x0900]);         // systolic cuff pressure
        Assert.Equal(new LsNetAlarmLimit(0x0B00, 380, null), limits[0x0B00]);       // skin temperature, in tenths
        Assert.Equal(new LsNetAlarmLimit(0x2C02, null, 60), limits[0x2C02]);        // mean arterial pressure
        Assert.Equal(new LsNetAlarmLimit(0x0301, null, null), limits[0x0301]);      // ST II: both off
        Assert.Equal(new LsNetAlarmLimit(0x0401, 20, null), limits[0x0401]);        // apnoea after 20 s; it has no lower limit (7FFF)
    }

    [Fact]
    public void A_change_of_limits_on_the_monitor_comes_unasked_as_the_same_table()
    {
        var push = Parsed("limits-push");
        Assert.Equal(LsNetKind.Push, push.Kind);
        Assert.Equal(LsNetProtocol.LimitsPushType, push.Type);

        // Sent after the SpO2 lower limit was raised to 99 to put the value of 98 in alarm.
        var limits = LimitTable("limits-push");
        Assert.Equal(100, limits.Count);
        Assert.Equal(new LsNetAlarmLimit(0x0700, 100, 99), limits[0x0700]);
        Assert.Equal(new LsNetAlarmLimit(0x0100, 144, 49), limits[0x0100]);
    }

    [Fact]
    public void A_limits_table_that_does_not_end_with_the_datagram_is_not_read()
    {
        var table = (byte[])Wire["limits-response"].Clone();
        table[32] = 99;                             // one record fewer than there is room for

        var answer = LsNetProtocol.Parse(table)!;

        Assert.Equal(LsNetKind.Response, answer.Kind);
        Assert.Null(answer.AlarmLimits);
        Assert.Null(Parsed("patient-response").AlarmLimits);       // nor is any other answer taken for one
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
        public void DeliverBytes(byte[] datagram, int port = LsNetProtocol.StreamPort) =>
            _incoming.Writer.TryWrite(new LsNetReceived(Monitor, port, datagram));
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

    [Fact]
    public async Task A_limit_alarm_names_the_value_it_is_on()
    {
        var transport = new FakeTransport();
        using var provider = Provider(transport);
        var messages = Collect(provider);
        provider.Start();

        transport.Deliver("alarm-crisis-spo2");
        await Wait.UntilAsync(() => Alarms(messages).Count == 1, "the SpO2 alarm is passed on");
        transport.Deliver("alarm-warning-pr");
        await Wait.UntilAsync(() => Alarms(messages).Count == 2, "the pulse rate alarm is passed on");
        transport.Deliver("alarm-advisory-temp");
        await Wait.UntilAsync(() => Alarms(messages).Count == 3, "the temperature alarm is passed on");

        // Under the ids its value is read by: SpO2, the pulse rate of the arterial line, and
        // the skin temperature, which is the first temperature of that report.
        Assert.Equal(
            new int?[] { 160, 600, 200 },
            Alarms(messages).Select(m => m.Oru!.Alarms.Single().ParameterId));
    }

    private static List<UmecMessage> Technical(List<UmecMessage> messages)
    {
        lock (messages) { return messages.Where(m => m.Kind == UmecMessageKind.TechnicalAlarms).ToList(); }
    }

    // ------------------------------------------------------------ the alarm limits

    private static List<UmecMessage> LimitSettings(List<UmecMessage> messages)
    {
        lock (messages) { return messages.Where(m => m.Kind == UmecMessageKind.AlarmLimits).ToList(); }
    }

    /// <summary>The types of the requests sent so far, the subscription and its goodbye aside.</summary>
    private static HashSet<int> RequestTypes(FakeTransport transport) => transport.Sent
        .Where(s => s.Data.Length >= 32 && s.Data[0] == 0x00)
        .Select(s => (s.Data[12] << 8) | s.Data[13])
        .Where(type => type != LsNetProtocol.SubscribeType)
        .ToHashSet();

    [Fact]
    public async Task A_streaming_monitor_is_asked_for_its_alarm_limits()
    {
        var transport = new FakeTransport();
        using var provider = Provider(transport);
        provider.Start();

        await Wait.UntilAsync(() => transport.Count(LsNetProtocol.SubscribeType) >= 2, "the stream is asked for, twice");
        Assert.Equal(0, transport.Count(LsNetProtocol.LimitsType));     // a monitor that is not there is asked for its stream only

        transport.Deliver("wave1-1");
        await Wait.UntilAsync(() => transport.Count(LsNetProtocol.LimitsType) >= 1, "the limits are asked for");

        // As the monitor answered it, but for the sequence number.
        var sent = transport.Sent.First(s => s.Data.Length == 36);
        Assert.Equal(Monitor, sent.To);
        Assert.Equal(Wire["limits-request-sent"].AsSpan(4).ToArray(), sent.Data.AsSpan(4).ToArray());
        Assert.Equal(Wire["limits-request-sent"].AsSpan(0, 2).ToArray(), sent.Data.AsSpan(0, 2).ToArray());
    }

    [Fact]
    public async Task Nothing_else_of_what_a_central_station_asks_is_sent()
    {
        var transport = new FakeTransport();
        using var provider = Provider(transport, o => { o.AlarmLimitsIntervalMs = 1000; o.StreamTimeoutMs = 5000; });
        provider.Start();

        transport.Deliver("wave1-1");
        await Wait.UntilAsync(() => transport.Count(LsNetProtocol.LimitsType) >= 2, "the limits are asked for again", timeoutMs: 5000);

        // The patient and the limits, and that is all. Above all not 0206, "monitor this bed":
        // the BSM-3562 answers it by taking the waveforms out of the stream.
        Assert.Equal(new HashSet<int> { LsNetProtocol.PatientType, LsNetProtocol.LimitsType }, RequestTypes(transport));
    }

    [Fact]
    public async Task With_the_limits_turned_off_the_monitor_is_asked_for_its_stream_and_its_patient_only()
    {
        var transport = new FakeTransport();
        using var provider = Provider(transport, o => o.ReadAlarmLimits = false);
        provider.Start();

        transport.Deliver("wave1-1");
        await Wait.UntilAsync(() => transport.Count(LsNetProtocol.PatientType) >= 1, "the patient is asked for");

        Assert.Equal(new HashSet<int> { LsNetProtocol.PatientType }, RequestTypes(transport));
    }

    [Fact]
    public async Task A_stream_that_lost_its_waveforms_is_asked_for_them_again()
    {
        var transport = new FakeTransport();
        using var provider = Provider(transport, o => o.StreamTimeoutMs = 5000);
        var messages = Collect(provider);
        provider.Start();

        // The stream is there, so the subscription is not repeated for silence...
        transport.Deliver("wave3-1");
        await Wait.UntilAsync(() => provider.ConnectedDevices.Count == 1, "the monitor is streaming");
        int asked = transport.Count(LsNetProtocol.SubscribeType);

        // ...but for three seconds it brings values and no waveform, from a monitor that says
        // it has ECG II, pleth and the pressures to give.
        for (int second = 0; second < 3; second++)
        {
            for (int i = 0; i < 3; i++) transport.Deliver("stream-emptied");
            transport.Deliver("stream-emptied-values");
        }

        await Wait.UntilAsync(
            () =>
            {
                if (transport.Count(LsNetProtocol.SubscribeType) > asked) return true;

                // The empty packets keep coming meanwhile: this is not a monitor gone silent,
                // which is asked again for another reason.
                transport.Deliver("stream-emptied");
                return false;
            },
            "the waveforms are asked for again");
        Assert.Equal(asked + 1, transport.Count(LsNetProtocol.SubscribeType));

        // The values went to the feed all the while: the patient was never without numbers.
        await Wait.UntilAsync(() => Reports(messages).Count == 3, "the three reports are passed on");
        Assert.All(Reports(messages), m => Assert.Equal(80, m.Oru!.Vitals.Single(v => v.ParameterId == 101).Value));
    }

    [Fact]
    public async Task An_emptied_stream_found_at_start_is_asked_for_its_waveforms_too()
    {
        var transport = new FakeTransport();
        using var provider = Provider(transport, o => o.StreamTimeoutMs = 5000);
        var messages = Collect(provider);

        // The monitor is still streaming to this address when the server starts - to the one
        // that ran before it, or to a probe - and the stream has no waveforms in it. The
        // packets are there before anything was asked for.
        for (int second = 0; second < 3; second++) transport.Deliver("stream-emptied-values");
        provider.Start();

        await Wait.UntilAsync(
            () =>
            {
                if (transport.Count(LsNetProtocol.SubscribeType) >= 1) return true;
                transport.Deliver("stream-emptied");
                return false;
            },
            "the waveforms are asked for");

        // And the driver is still reading: the next report is passed on like the three before.
        transport.Deliver("wave1-0");
        await Wait.UntilAsync(() => Reports(messages).Count == 4, "the next report is passed on");
        Assert.Single(provider.ConnectedDevices);
    }

    [Fact]
    public async Task A_stream_with_its_waveforms_is_left_alone()
    {
        var transport = new FakeTransport();
        using var provider = Provider(transport, o => o.StreamTimeoutMs = 5000);
        var messages = Collect(provider);
        provider.Start();

        for (int i = 0; i < 5; i++) transport.Deliver("wave1-0");
        await Wait.UntilAsync(() => Reports(messages).Count == 5, "five reports are passed on");
        int asked = transport.Count(LsNetProtocol.SubscribeType);

        // Two reports without waveforms and then one with: not three in a row, nothing to ask.
        transport.Deliver("stream-emptied-values");
        transport.Deliver("stream-emptied-values");
        transport.Deliver("wave1-0");
        transport.Deliver("stream-emptied-values");
        transport.Deliver("stream-emptied-values");
        await Wait.UntilAsync(() => Reports(messages).Count == 10, "the next five are passed on");
        await Task.Delay(300);

        Assert.Equal(asked, transport.Count(LsNetProtocol.SubscribeType));
    }

    [Fact]
    public async Task The_limits_reach_the_feed_as_the_alarm_settings_of_the_values_it_reads()
    {
        var transport = new FakeTransport();
        using var provider = Provider(transport, o => o.StreamTimeoutMs = 5000);
        var messages = Collect(provider);
        provider.Start();

        transport.Deliver("wave1-1");
        await Wait.UntilAsync(() => provider.ConnectedDevices.Count == 1, "the monitor is streaming");
        transport.Deliver("limits-response", port: LsNetProtocol.ControlPort);
        await Wait.UntilAsync(() => LimitSettings(messages).Count == 1, "the limits are passed on");

        var first = LimitSettings(messages)[0];
        Assert.Equal("10.10.46.48", first.SourceIp);
        var settings = first.Oru!.AlarmSettings.ToDictionary(s => s.ParameterId, s => (s.LowLimit, s.HighLimit));
        Assert.Equal((50, 140), settings[101]);                                  // heart rate
        Assert.Equal((90, null), settings[160]);                                 // SpO2
        Assert.Equal((80, 180), settings[170]);                                  // systolic cuff pressure
        Assert.Equal((null, null), settings[171]);                               // diastolic: listed, both limits off
        Assert.Equal((80, null), settings[ServerParameterIds.ArtSystolic]);
        Assert.Equal((60, null), settings[ServerParameterIds.ArtMean]);
        Assert.Equal((null, null), settings[106]);                               // ST-II
        // The table has a record for every temperature site; which of them is the first
        // temperature is in a report, and none has come yet.
        Assert.DoesNotContain(200, settings.Keys);

        // The report: skin temperature first, rectal second. Their limits follow, in degrees.
        transport.Deliver("wave1-0");
        await Wait.UntilAsync(() => LimitSettings(messages).Count == 2, "the limits of the temperatures are added");
        settings = LimitSettings(messages)[1].Oru!.AlarmSettings.ToDictionary(s => s.ParameterId, s => (s.LowLimit, s.HighLimit));
        Assert.Equal((null, 38.0), settings[200]);
        Assert.Equal((null, 38.0), settings[201]);
        Assert.Equal((50, 140), settings[101]);

        // The poll brings the same table every two seconds: no news, nothing passed on.
        transport.Deliver("limits-response", port: LsNetProtocol.ControlPort);
        transport.Deliver("wave1-4");
        await Wait.UntilAsync(() => Reports(messages).Count == 2, "the next report is passed on");
        Assert.Equal(2, LimitSettings(messages).Count);

        // The limits are changed on the monitor, and it says so itself.
        transport.Deliver("limits-push", port: LsNetProtocol.ControlPort);
        await Wait.UntilAsync(() => LimitSettings(messages).Count == 3, "the changed limits are passed on");
        settings = LimitSettings(messages)[2].Oru!.AlarmSettings.ToDictionary(s => s.ParameterId, s => (s.LowLimit, s.HighLimit));
        Assert.Equal((99, 100), settings[160]);
        Assert.Equal((49, 144), settings[101]);
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
    public async Task While_the_cuff_is_inflated_the_report_says_a_measurement_is_running()
    {
        var transport = new FakeTransport();
        using var provider = Provider(transport);
        var messages = Collect(provider);
        provider.Start();

        transport.Deliver("technical-none");
        await Wait.UntilAsync(() => Reports(messages).Count == 1, "the report before the measurement is passed on");
        Assert.False(Reports(messages)[0].Oru!.NibpMeasuring);

        transport.Deliver("technical-nibp-measuring");
        await Wait.UntilAsync(() => Reports(messages).Count == 2, "the report during the measurement is passed on");
        var during = Reports(messages)[1].Oru!;
        Assert.True(during.NibpMeasuring);

        // The reading before is still on the monitor, and goes with it: 117/58, mean 77.
        var cuff = during.Vitals.Where(v => v.ParameterId is 170 or 171 or 172).ToDictionary(v => v.ParameterId, v => v.Value);
        Assert.Equal((117.0, 58.0, 77.0), (cuff[170], cuff[171], cuff[172]));

        transport.Deliver("technical-none");
        await Wait.UntilAsync(() => Reports(messages).Count == 3, "the report after the measurement is passed on");
        Assert.False(Reports(messages)[2].Oru!.NibpMeasuring);
    }

    [Fact]
    public async Task A_first_measurement_is_reported_as_running_with_no_reading_to_show()
    {
        var transport = new FakeTransport();
        using var provider = Provider(transport);
        var messages = Collect(provider);
        provider.Start();

        // 2026-11-26: nothing measured yet, then NIBP Start pressed on the monitor.
        transport.Deliver("cuff-idle-no-reading");
        await Wait.UntilAsync(() => Reports(messages).Count == 1, "the report before the measurement is passed on");
        Assert.False(Reports(messages)[0].Oru!.NibpMeasuring);

        transport.Deliver("cuff-measuring-no-reading");
        await Wait.UntilAsync(() => Reports(messages).Count == 2, "the report during the measurement is passed on");
        var during = Reports(messages)[1].Oru!;
        Assert.True(during.NibpMeasuring);

        // The three pressures are listed, without a number: there is no reading before to show.
        var cuff = during.Vitals.Where(v => v.ParameterId is 170 or 171 or 172).ToList();
        Assert.Equal(3, cuff.Count);
        Assert.All(cuff, v => Assert.Null(v.Value));

        // And it is a state, not an alarm.
        await Wait.UntilAsync(() => Technical(messages).Count >= 1, "the technical list is passed on");
        Assert.All(Technical(messages), m => Assert.DoesNotContain(m.Oru!.Alarms, a => a.Text.Contains("NIBP")));
    }

    [Fact]
    public async Task The_pressure_in_the_cuff_is_not_passed_on_as_a_value_of_the_patient()
    {
        var transport = new FakeTransport();
        using var provider = Provider(transport);
        var messages = Collect(provider);
        provider.Start();

        // No capture has code 0909, by the station software's names the pressure in the cuff
        // while it is inflated. The record is made here, out of the cuff mode (090A, the last
        // but one) of a real report: 150 mmHg in the cuff must not turn up among the readings,
        // least of all as a pulse rate, which this code was once taken for.
        var report = (byte[])Wire["technical-nibp-measuring"].Clone();
        int record = report.Length - 2 * 10;
        Assert.Equal((0x09, 0x0A), (report[record], report[record + 1]));
        report[record + 1] = 0x09;
        report[record + 9] = 150;

        transport.DeliverBytes(report);
        await Wait.UntilAsync(() => Reports(messages).Count == 1, "the report is passed on");

        var vitals = Reports(messages)[0].Oru!.Vitals;
        Assert.DoesNotContain(vitals, v => v.Value == 150);
        Assert.Equal(new[] { 101, 102, 106, 151, 170, 171, 172 }, vitals.Select(v => v.ParameterId).OrderBy(id => id));
    }

    // ------------------------------------------------------------ the cuff, started from a station

    private const string MonitorIp = "10.10.46.48";

    /// <summary>The 0403 datagrams that went out: kind 01 00, 40 bytes.</summary>
    private static List<(byte[] Data, IPAddress To)> CuffCommands(FakeTransport transport) =>
        transport.Sent.Where(s => s.Data.Length == 40 && s.Data[0] == 0x01 && s.Data[12] == 0x04 && s.Data[13] == 0x03).ToList();

    /// <summary>
    /// A provider that may send cuff commands. The monitor counts as streaming for five seconds
    /// after one packet, so a test can deliver one and then ask.
    /// </summary>
    private static NihonKohdenLsNetProvider CuffProvider(FakeTransport transport, bool allowed = true) =>
        Provider(transport, o => { o.RemoteNibp = allowed; o.StreamTimeoutMs = 5000; });

    [Fact]
    public async Task A_station_can_have_a_streaming_monitor_start_its_cuff()
    {
        var transport = new FakeTransport();
        using var provider = CuffProvider(transport);
        provider.Start();

        transport.Deliver("cuff-idle-no-reading");
        await Wait.UntilAsync(() => provider.ConnectedDevices.Count == 1, "the monitor is streaming");

        Assert.True(provider.CanControlCuff(MonitorIp));
        Assert.Equal(CuffRequestStatus.Accepted, provider.RequestCuff(MonitorIp, start: true));

        await Wait.UntilAsync(() => CuffCommands(transport).Count == 1, "the command goes out");
        var sent = CuffCommands(transport)[0];
        Assert.Equal(Monitor, sent.To);

        // The bytes the monitor took on 2026-11-27, but for the sequence number.
        Assert.Equal(Wire["cuff-start-sent"].AsSpan(0, 2).ToArray(), sent.Data.AsSpan(0, 2).ToArray());
        Assert.Equal(Wire["cuff-start-sent"].AsSpan(4).ToArray(), sent.Data.AsSpan(4).ToArray());

        // Once. The monitor has not answered and its reports show nothing: it is still not repeated.
        await Task.Delay(400);
        Assert.Single(CuffCommands(transport));
    }

    [Fact]
    public async Task Unless_it_was_turned_on_no_cuff_command_ever_leaves()
    {
        var transport = new FakeTransport();
        using var provider = CuffProvider(transport, allowed: false);
        provider.Start();

        transport.Deliver("cuff-idle-no-reading");
        await Wait.UntilAsync(() => provider.ConnectedDevices.Count == 1, "the monitor is streaming");

        Assert.False(new LsNetOptions().RemoteNibp);        // and off is what it is unless somebody says otherwise
        Assert.False(provider.CanControlCuff(MonitorIp));
        Assert.Equal(CuffRequestStatus.NotAllowed, provider.RequestCuff(MonitorIp, start: true));
        Assert.Equal(CuffRequestStatus.NotAllowed, provider.RequestCuff(MonitorIp, start: false));

        await Task.Delay(300);
        Assert.Empty(CuffCommands(transport));
    }

    [Fact]
    public async Task A_start_is_refused_while_a_measurement_runs_and_a_stop_is_passed_on()
    {
        var transport = new FakeTransport();
        using var provider = CuffProvider(transport);
        var messages = Collect(provider);
        provider.Start();

        transport.Deliver("cuff-measuring-no-reading");
        await Wait.UntilAsync(() => Reports(messages).Count == 1, "the report of the running measurement is in");

        Assert.Equal(CuffRequestStatus.AlreadyMeasuring, provider.RequestCuff(MonitorIp, start: true));
        Assert.Equal(CuffRequestStatus.Accepted, provider.RequestCuff(MonitorIp, start: false));

        await Wait.UntilAsync(() => CuffCommands(transport).Count == 1, "the stop goes out");
        Assert.Equal(
            LsNetProtocol.CuffControl(start: false, 1, MonitorId, Self).AsSpan(4).ToArray(),
            CuffCommands(transport)[0].Data.AsSpan(4).ToArray());
    }

    [Fact]
    public async Task A_second_start_on_the_heels_of_the_first_is_not_sent()
    {
        var transport = new FakeTransport();
        using var provider = CuffProvider(transport);
        provider.Start();

        transport.Deliver("cuff-idle-no-reading");
        await Wait.UntilAsync(() => provider.ConnectedDevices.Count == 1, "the monitor is streaming");

        Assert.Equal(CuffRequestStatus.Accepted, provider.RequestCuff(MonitorIp, start: true));
        Assert.Equal(CuffRequestStatus.TooSoon, provider.RequestCuff(MonitorIp, start: true));

        await Wait.UntilAsync(() => CuffCommands(transport).Count >= 1, "the first start goes out");
        await Task.Delay(300);
        Assert.Single(CuffCommands(transport));
    }

    [Fact]
    public async Task A_monitor_that_is_not_streaming_or_is_not_this_drivers_is_told_nothing()
    {
        var transport = new FakeTransport();
        using var provider = CuffProvider(transport);
        provider.Start();
        await Wait.UntilAsync(() => transport.Count(LsNetProtocol.SubscribeType) >= 1, "the monitor is asked for its stream");

        Assert.Equal(CuffRequestStatus.NotStreaming, provider.RequestCuff(MonitorIp, start: true));
        Assert.Equal(CuffRequestStatus.NotSupported, provider.RequestCuff("10.10.46.49", start: true));     // on HL7
        Assert.Equal(CuffRequestStatus.NotSupported, provider.RequestCuff("192.168.2.101", start: true));
        Assert.Equal(CuffRequestStatus.NotSupported, provider.RequestCuff("not an address", start: true));
        Assert.False(provider.CanControlCuff("10.10.46.49"));

        await Task.Delay(200);
        Assert.Empty(CuffCommands(transport));
    }

    private sealed class CapturedLog : Microsoft.Extensions.Logging.ILogger<NihonKohdenLsNetProvider>
    {
        private readonly List<(Microsoft.Extensions.Logging.LogLevel Level, string Message)> _entries = new();

        public IReadOnlyList<(Microsoft.Extensions.Logging.LogLevel Level, string Message)> Entries
        {
            get { lock (_entries) { return _entries.ToArray(); } }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => logLevel >= Microsoft.Extensions.Logging.LogLevel.Information;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_entries) { _entries.Add((logLevel, formatter(state, exception))); }
        }
    }

    private static NihonKohdenLsNetProvider LoggedCuffProvider(FakeTransport transport, CapturedLog log) =>
        new(
            new LsNetOptions { LocalIp = "10.200.200.77", BeaconIntervalMs = 20, StreamTimeoutMs = 5000, RemoteNibp = true },
            new[] { new NihonKohdenMonitor { Name = "BSM-3562", Ip = MonitorIp, Protocol = "LsNet" } },
            log,
            (_, _) => transport);

    [Fact]
    public async Task The_log_follows_a_start_to_the_measurement_it_set_off()
    {
        var transport = new FakeTransport();
        var log = new CapturedLog();
        using var provider = LoggedCuffProvider(transport, log);
        provider.Start();

        transport.Deliver("cuff-idle-no-reading");
        await Wait.UntilAsync(() => provider.ConnectedDevices.Count == 1, "the monitor is streaming");

        Assert.Equal(CuffRequestStatus.Accepted, provider.RequestCuff(MonitorIp, start: true));
        await Wait.UntilAsync(() => CuffCommands(transport).Count == 1, "the command goes out");

        // As on 2026-11-27: the monitor pushes the record back, then its report shows the cuff at work.
        transport.Deliver("cuff-started-push", port: LsNetProtocol.ControlPort);
        transport.Deliver("cuff-started-push", port: LsNetProtocol.ControlPort);
        transport.Deliver("cuff-measuring-no-reading");

        await Wait.UntilAsync(() => log.Entries.Any(e => e.Message.Contains("reports a cuff measurement running")), "the outcome is logged");
        var lines = log.Entries.Select(e => e.Message).ToList();
        Assert.Single(lines, l => l.Contains($"monitor {MonitorIp} was told to start a cuff measurement"));
        Assert.Single(lines, l => l.Contains("acknowledged the start of a cuff measurement"));      // once, though it came twice
        Assert.DoesNotContain(log.Entries, e => e.Level >= Microsoft.Extensions.Logging.LogLevel.Warning && e.Message.Contains("cuff"));
    }

    [Fact]
    public async Task A_start_the_monitor_does_nothing_with_is_said_so_once_and_not_sent_again()
    {
        var transport = new FakeTransport();
        var log = new CapturedLog();
        using var provider = LoggedCuffProvider(transport, log);
        provider.CuffAnswerWait = TimeSpan.FromMilliseconds(250);
        provider.Start();

        transport.Deliver("cuff-idle-no-reading");
        await Wait.UntilAsync(() => provider.ConnectedDevices.Count == 1, "the monitor is streaming");
        Assert.Equal(CuffRequestStatus.Accepted, provider.RequestCuff(MonitorIp, start: true));

        // The monitor goes on reporting a cuff at rest.
        for (int i = 0; i < 8; i++)
        {
            transport.Deliver("cuff-idle-no-reading");
            await Task.Delay(60);
        }

        await Wait.UntilAsync(
            () => log.Entries.Any(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning && e.Message.Contains("its reports do not show it")),
            "the missing effect is logged");

        await Task.Delay(400);
        Assert.Single(log.Entries, e => e.Message.Contains("its reports do not show it"));
        Assert.Contains(log.Entries, e => e.Message.Contains("did not acknowledge the command"));
        Assert.Single(CuffCommands(transport));
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
