using MedicalMonitorServer.Contracts.Models;
using MedicalMonitorServer.DeviceDriver.NihonKohden;

namespace MedicalMonitorServer.DeviceDriver.Tests;

/// <summary>
/// The Nihon Kohden driver against what a real BSM-3562 (SW 07-41) put on the wire on
/// 2026-11-21: the fixtures are the raw TCP stream of its ORU port, MLLP framing included.
/// "simulation" is three minutes with the monitor's simulation mode on; "no_sensors" is two
/// minutes with nothing attached, the second after an NIBP measurement.
/// </summary>
public class NihonKohdenTests
{
    private static readonly NihonKohdenParser Parser = new();

    private static List<UmecMessage> Load(string name)
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
        return new ComunicationService().Feed(bytes).Select(frame => Parser.Parse(frame)!).ToList();
    }

    private static Dictionary<int, VitalSign> VitalsOf(UmecMessage message) =>
        message.Oru!.Vitals.ToDictionary(v => v.ParameterId);

    [Fact]
    public void Simulation_stream_is_three_vitals_messages()
    {
        var messages = Load("nihon_kohden_bsm3562_simulation.bin");

        Assert.Equal(3, messages.Count);
        Assert.All(messages, m =>
        {
            Assert.Equal("ORU^R01^ORU_R01", m.MessageType);
            Assert.Equal("2.4", m.Hl7Version);
            Assert.Equal(UmecMessageKind.Vitals, m.Kind);
        });
        Assert.Equal(
            new[] { "20261121000001", "20261121000002", "20261121000003" },
            messages.Select(m => m.ControlId));
    }

    [Fact]
    public void Values_carry_the_parameter_ids_the_feed_reads()
    {
        var vitals = VitalsOf(Load("nihon_kohden_bsm3562_simulation.bin")[0]);

        Assert.Equal(80, vitals[101].Value);        // 001000 VITAL HR
        Assert.Equal(0, vitals[102].Value);         // 002000 VITAL VPC
        Assert.Equal(0.05, vitals[106].Value);      // 003001 VITAL ST2
        Assert.Equal(-0.03, vitals[108].Value);     // 003003 VITAL STVR
        Assert.Equal(98, vitals[160].Value);        // 007000 VITAL SpO2
        Assert.Equal(80, vitals[161].Value);        // 007001 VITAL PR(spo2)
        Assert.Equal(80, vitals[600].Value);        // 044006 VITAL PR(art)
        Assert.Equal("bpm", vitals[101].Unit);
        Assert.Equal("VITAL HR", vitals[101].Label);
        Assert.Equal(new DateTime(2026, 11, 21, 13, 25, 0), vitals[101].MeasuredAt);
    }

    [Fact]
    public void Temperatures_are_numbered_in_the_order_sent()
    {
        var vitals = VitalsOf(Load("nihon_kohden_bsm3562_simulation.bin")[0]);

        Assert.Equal(36.5, vitals[200].Value);      // 011000 VITAL TSKIN
        Assert.Equal(37.0, vitals[201].Value);      // 014000 VITAL TRECT
        Assert.Equal("°C", vitals[200].Unit);
    }

    [Fact]
    public void Nibp_is_aperiodic_and_keeps_the_time_it_was_taken()
    {
        var vitals = VitalsOf(Load("nihon_kohden_bsm3562_simulation.bin")[2]);

        Assert.Equal(136, vitals[170].Value);
        Assert.Equal(75, vitals[171].Value);
        Assert.Equal(95, vitals[172].Value);
        Assert.All(new[] { 170, 171, 172 }, id =>
        {
            Assert.True(vitals[id].IsAperiodic);
            Assert.Equal(new DateTime(2026, 11, 21, 13, 18, 33), vitals[id].MeasuredAt);
        });
        Assert.False(vitals[101].IsAperiodic);
    }

    [Fact]
    public void Invasive_pressures_reach_the_feed_under_the_servers_own_ids()
    {
        var vitals = VitalsOf(Load("nihon_kohden_bsm3562_simulation.bin")[0]);

        // 044000-044002 the arterial line, 053000-053002 the central venous pressure.
        Assert.Equal(118, vitals[ServerParameterIds.ArtSystolic].Value);
        Assert.Equal(76, vitals[ServerParameterIds.ArtDiastolic].Value);
        Assert.Equal(91, vitals[ServerParameterIds.ArtMean].Value);
        Assert.Equal(8, vitals[ServerParameterIds.CvpMean].Value);
        Assert.Equal("mmHg", vitals[ServerParameterIds.ArtSystolic].Unit);
    }

    [Fact]
    public void Measurements_without_a_counterpart_stay_observations_only()
    {
        var oru = Load("nihon_kohden_bsm3562_simulation.bin")[0].Oru!;

        // 26 VITAL + 3 NIBP on the wire; the second pulse rate of the arterial line is read by nobody.
        Assert.Equal(29, oru.Observations.Count);
        Assert.Equal(28, oru.Vitals.Count);
        Assert.Contains(oru.Observations, o => o.Code == "072044");
        Assert.DoesNotContain(oru.Vitals, v => v.Label.Contains("rPR"));
    }

    [Fact]
    public void Bed_comes_from_PV1_and_the_monitor_says_it_has_no_patient_id()
    {
        var patient = Load("nihon_kohden_bsm3562_simulation.bin")[0].Oru!.Patient!;

        Assert.True(patient.PidPresent);
        Assert.Equal("", patient.PatientId);
        Assert.Equal("ICU-001", patient.BedLabel);
        Assert.Equal("1", patient.BedNumber);
        Assert.Equal("10.10.46.48", patient.MonitorIp);
        Assert.Equal("", patient.Department);
    }

    [Fact]
    public void A_minute_with_no_sensor_attached_is_a_message_without_values()
    {
        var messages = Load("nihon_kohden_bsm3562_no_sensors.bin");

        Assert.Equal(2, messages.Count);
        Assert.Empty(messages[0].Oru!.Vitals);
        Assert.True(messages[0].Oru!.VitalsAreComplete);   // "nothing" is the report, not a gap in it
        Assert.Equal(UmecMessageKind.Vitals, messages[0].Kind);
        Assert.Equal(new[] { 170, 171, 172 }, messages[1].Oru!.Vitals.Select(v => v.ParameterId));
    }

    [Fact]
    public void Ack_answers_the_control_id_with_the_parties_swapped()
    {
        const string oru =
            "MSH|^~\\&|NIHON KOHDEN|NIHON KOHDEN|CLIENT APP|CLIENT FACILITY|20261121131900||ORU^R01^ORU_R01|20261121000003|P|2.4|||NE|AL||ASCII||ASCII\r" +
            "PID||||||||O\r";

        var ack = NihonKohdenDeviceDataProvider.BuildAck(oru, new DateTime(2026, 11, 21, 13, 19, 1));

        Assert.Equal(
            "MSH|^~\\&|CLIENT APP|CLIENT FACILITY|NIHON KOHDEN|NIHON KOHDEN|20261121131901||ACK^R01|20261121000003|P|2.4\r" +
            "MSA|AA|20261121000003\r",
            ack);
    }

    [Fact]
    public void Nothing_to_acknowledge_without_a_control_id()
    {
        Assert.Null(NihonKohdenDeviceDataProvider.BuildAck("not hl7 at all", DateTime.Now));
    }

    // ------------------------------------------------------------ the link

    private const string Oru =
        "MSH|^~\\&|NIHON KOHDEN|NIHON KOHDEN|CLIENT APP|CLIENT FACILITY|20261121132500||ORU^R01^ORU_R01|20261121000001|P|2.4|||NE|AL||ASCII||ASCII\r" +
        "PID||||||||O\r" +
        "PV1||I|^^ICU-001^10.10.46.48:1\r" +
        "ORC|RE\r" +
        "OBR|1|||VITAL|||20261121132500||||||||||||||||||A\r" +
        "OBX|1|NM|001000^VITAL HR|1|80|bpm|||||F|||20261121132500|||\r";

    private static NihonKohdenDeviceDataProvider Provider(FakeReader reader) => new(
        new NihonKohdenOptions
        {
            Monitors = { new NihonKohdenMonitor { Name = "BSM-3562", Ip = "10.10.46.48", Port = 7998 } },
            ReconnectDelayMs = 10,
        },
        readerFactory: _ => reader);

    [Fact]
    public async Task Each_message_is_acknowledged_and_passed_on_with_its_source()
    {
        var reader = new FakeReader(FakeReader.Delivers(Oru));
        using var provider = Provider(reader);
        var received = new List<UmecMessage>();
        provider.OnMessageReceived += m => { lock (received) { received.Add(m); } };

        provider.Start();

        await Wait.UntilAsync(() => { lock (received) { return received.Count == 1; } }, "the message is passed on");
        var session = reader.Sessions[0];
        Assert.Equal(("10.10.46.48", 7998), (session.Ip, session.Port));

        var ack = Assert.Single(session.Link.SentText);
        Assert.Contains("|ACK^R01|20261121000001|P|2.4\rMSA|AA|20261121000001\r", ack);

        lock (received)
        {
            Assert.Equal("10.10.46.48", received[0].SourceIp);
            Assert.Equal(80, Assert.Single(received[0].Oru!.Vitals).Value);
        }
    }

    [Fact]
    public async Task The_monitor_is_listed_while_its_stream_is_open()
    {
        var reader = new FakeReader(FakeReader.StaysOpen);
        using var provider = Provider(reader);
        var up = new List<DeviceState>();
        provider.OnDeviceConnected += (_, state) => { lock (up) { up.Add(state); } };

        provider.Start();

        await Wait.UntilAsync(() => { lock (up) { return up.Count == 1; } }, "the monitor is reported connected");
        var listed = Assert.Single(provider.ConnectedDevices);
        Assert.Equal(("BSM-3562", "10.10.46.48", "7998", DeviceStateEnum.Connected),
            (listed.Name, listed.IpAddress, listed.Port, listed.State));
    }

    [Fact]
    public async Task A_closed_stream_is_dialled_again_and_never_given_up_on()
    {
        var reader = new FakeReader(FakeReader.Fails, FakeReader.EndsImmediately, FakeReader.Fails, FakeReader.StaysOpen);
        using var provider = Provider(reader);
        var events = new List<string>();
        provider.OnDeviceConnected += (_, _) => { lock (events) { events.Add("up"); } };
        provider.OnDeviceDisconnected += (_, _) => { lock (events) { events.Add("down"); } };

        provider.Start();

        await Wait.UntilAsync(() => reader.Attempts == 4 && provider.ConnectedDevices.Count == 1, "the fourth attempt holds");
        lock (events) { Assert.Equal("up", events[^1]); }
    }

    [Fact]
    public async Task Between_reports_a_live_link_keeps_the_monitor_from_looking_silent()
    {
        var reader = new FakeReader(FakeReader.Delivers(Oru));
        using var provider = Provider(reader);
        provider.LinkAliveInterval = TimeSpan.FromMilliseconds(20);
        var seen = new List<UmecMessage>();
        provider.OnMessageReceived += m => { lock (seen) { seen.Add(m); } };

        provider.Start();

        await Wait.UntilAsync(() => { lock (seen) { return seen.Count(m => m.Kind == UmecMessageKind.MonitorStatus) >= 3; } },
            "the link vouches for the monitor while the next report is due");
        lock (seen)
        {
            var alive = seen.First(m => m.Kind == UmecMessageKind.MonitorStatus);
            Assert.Equal("10.10.46.48", alive.SourceIp);
            Assert.Null(alive.Oru);
        }
    }

    [Fact]
    public async Task A_monitor_that_misses_its_report_is_no_longer_vouched_for()
    {
        var reader = new FakeReader(FakeReader.StaysOpen);
        using var provider = Provider(reader);
        provider.LinkAliveInterval = TimeSpan.FromMilliseconds(20);
        provider.ReportGrace = TimeSpan.FromMilliseconds(150);
        int alive = 0;
        provider.OnMessageReceived += m => { if (m.Kind == UmecMessageKind.MonitorStatus) Interlocked.Increment(ref alive); };

        provider.Start();
        await Wait.UntilAsync(() => Volatile.Read(ref alive) >= 1, "a new link is given its first interval");

        await Task.Delay(400);          // well past the grace, with no report
        int after = Volatile.Read(ref alive);
        await Task.Delay(200);

        Assert.Equal(after, Volatile.Read(ref alive));
    }

    [Fact]
    public async Task Devices_found_by_the_watchdog_are_not_dialled()
    {
        var reader = new FakeReader(FakeReader.StaysOpen);
        using var provider = Provider(reader);

        provider.Connect(new DeviceState { IpAddress = "192.168.2.100", Port = "4601", State = DeviceStateEnum.Connected });
        await Task.Delay(50);

        Assert.Equal(0, reader.Attempts);
        Assert.Empty(provider.ConnectedDevices);
    }

    // ------------------------------------------------------------ writing the patient to the monitor

    private const string Accept = "MSH|^~\\&|NIHON KOHDEN|NIHON KOHDEN|CLIENT APP|CLIENT FACILITY|20261121130141||ACK^A04^ACK|X|P|2.4\rMSA|AA|X||||\r";
    private const string Refuse = "MSH|^~\\&|NIHON KOHDEN|NIHON KOHDEN|CLIENT APP|CLIENT FACILITY|20261121130132||ACK^A08^ACK|X|P|2.4\rMSA|AE|X||||\r";

    private static string OruFor(string patientId) => Oru.Replace("PID||||||||O", $"PID|||{patientId}|||||O");

    /// <summary>A provider that has heard one report from its monitor and records every ADT message it sends.</summary>
    private static async Task<(NihonKohdenDeviceDataProvider Provider, List<(int Port, string Message)> Sent)> ReportedAsync(
        string patientOnMonitor, Func<int, string, string?> answer)
    {
        var sent = new List<(int Port, string Message)>();
        var reader = new FakeReader(FakeReader.Delivers(OruFor(patientOnMonitor)));
        var provider = new NihonKohdenDeviceDataProvider(
            new NihonKohdenOptions { Monitors = { new NihonKohdenMonitor { Ip = "10.10.46.48" } }, ReconnectDelayMs = 10 },
            readerFactory: _ => reader,
            adtExchange: (_, port, message, _) =>
            {
                lock (sent) { sent.Add((port, message)); }
                return Task.FromResult(answer(port, message));
            });

        int received = 0;
        provider.OnMessageReceived += _ => Interlocked.Increment(ref received);
        provider.Start();
        await Wait.UntilAsync(() => Volatile.Read(ref received) == 1, "the monitor's report is in");
        return (provider, sent);
    }

    [Fact]
    public void Admit_and_update_are_PID_and_PV1_with_the_bed_name()
    {
        var now = new DateTime(2026, 11, 21, 13, 1, 41);

        Assert.Equal(
            "MSH|^~\\&|CLIENT APP|CLIENT FACILITY|NIHON KOHDEN|NIHON KOHDEN|20261121130141||ADT^A04^ADT_A01|MMS1|P|2.4\r" +
            "PID|||TEST010||SMITH^ANNA MARIA\r" +
            "PV1||I|^^ICU-001\r",
            NihonKohdenAdt.Admit("ICU-001", "TEST010", "SMITH ANNA MARIA", "MMS1", now));

        Assert.Contains("|ADT^A08^ADT_A01|MMS2|", NihonKohdenAdt.Update("ICU-001", "TEST010", "SMITH", "MMS2", now));
    }

    [Fact]
    public void A_name_that_is_not_plain_ASCII_is_left_out()
    {
        var message = NihonKohdenAdt.Admit("ICU-001", "P-17", "Шевченко Тарас", "MMS1", DateTime.Now);

        Assert.Contains("PID|||P-17||\r", message);
        Assert.All(message, c => Assert.True(c < 0x80));
    }

    [Theory]
    [InlineData("TEST010", true)]
    [InlineData("ANON-20663FB1", true)]
    [InlineData("PATIENT-12345678", true)]      // exactly the 16 characters the monitor keeps
    [InlineData("PATIENT-123456789", false)]    // one more would come back cut short
    [InlineData("", false)]
    [InlineData("A|B", false)]
    [InlineData("П-17", false)]
    public void Only_an_id_the_monitor_stores_unchanged_is_sent(string patientId, bool storable)
    {
        Assert.Equal(storable, NihonKohdenAdt.WhyIdCannotBeStored(patientId) is null);
    }

    [Fact]
    public async Task A_patient_without_an_id_on_the_monitor_is_named_by_an_update()
    {
        var (provider, sent) = await ReportedAsync(patientOnMonitor: "", (_, _) => Accept);
        using var _ = provider;

        var result = await provider.AssignPatientAsync("10.10.46.48", "P-17", "SMITH ANNA");

        Assert.True(result.Accepted);
        var only = Assert.Single(sent);
        Assert.Equal(9006, only.Port);
        Assert.Contains("|ADT^A08^ADT_A01|", only.Message);
        Assert.Contains("PID|||P-17||SMITH^ANNA\rPV1||I|^^ICU-001\r", only.Message);
    }

    [Fact]
    public async Task A_monitor_with_nobody_admitted_refuses_the_update_and_gets_an_admit()
    {
        var (provider, sent) = await ReportedAsync(patientOnMonitor: "", (port, _) => port == 9006 ? Refuse : Accept);
        using var _ = provider;

        var result = await provider.AssignPatientAsync("10.10.46.48", "P-17", "SMITH ANNA");

        Assert.True(result.Accepted);
        Assert.Equal(new[] { 9006, 9005 }, sent.Select(s => s.Port));
        Assert.Contains("|ADT^A04^ADT_A01|", sent[1].Message);
    }

    [Fact]
    public async Task Somebody_else_on_the_monitor_means_a_new_admission()
    {
        var (provider, sent) = await ReportedAsync(patientOnMonitor: "OLD-1", (_, _) => Accept);
        using var _ = provider;

        var result = await provider.AssignPatientAsync("10.10.46.48", "P-17", "SMITH ANNA");

        Assert.True(result.Accepted);
        var only = Assert.Single(sent);
        Assert.Equal(9005, only.Port);
        Assert.Contains("|ADT^A04^ADT_A01|", only.Message);
    }

    [Fact]
    public async Task The_patient_already_on_the_monitor_is_not_admitted_again()
    {
        var (provider, sent) = await ReportedAsync(patientOnMonitor: "P-17", (_, _) => Accept);
        using var _ = provider;

        var result = await provider.AssignPatientAsync("10.10.46.48", "P-17", "SMITH ANNA");

        Assert.True(result.Accepted);
        Assert.Empty(sent);
    }

    [Fact]
    public async Task An_id_the_monitor_would_cut_short_is_not_sent()
    {
        var (provider, sent) = await ReportedAsync(patientOnMonitor: "", (_, _) => Accept);
        using var _ = provider;

        var result = await provider.AssignPatientAsync("10.10.46.48", "PATIENT-1234567890-ABCDEF", "SMITH ANNA");

        Assert.Equal(MonitorAdmissionStatus.InvalidPatientId, result.Status);
        Assert.Empty(sent);
    }

    [Fact]
    public async Task A_monitor_that_does_not_answer_or_refuses_is_reported_as_such()
    {
        var (silent, _) = await ReportedAsync(patientOnMonitor: "OLD-1", (_, _) => null);
        using var first = silent;
        Assert.Equal(MonitorAdmissionStatus.Unreachable, (await silent.AssignPatientAsync("10.10.46.48", "P-17", "X")).Status);

        var (refusing, _) = await ReportedAsync(patientOnMonitor: "OLD-1", (_, _) => Refuse);
        using var second = refusing;
        Assert.Equal(MonitorAdmissionStatus.Rejected, (await refusing.AssignPatientAsync("10.10.46.48", "P-17", "X")).Status);
    }

    [Fact]
    public async Task Nothing_is_sent_before_the_monitor_has_told_its_bed_name_or_to_another_address()
    {
        var sent = 0;
        using var provider = new NihonKohdenDeviceDataProvider(
            new NihonKohdenOptions { Monitors = { new NihonKohdenMonitor { Ip = "10.10.46.48" } } },
            readerFactory: _ => new FakeReader(FakeReader.StaysOpen),
            adtExchange: (_, _, _, _) => { sent++; return Task.FromResult<string?>(Accept); });

        Assert.Equal(MonitorAdmissionStatus.BedUnknown, (await provider.AssignPatientAsync("10.10.46.48", "P-17", "X")).Status);
        Assert.Equal(MonitorAdmissionStatus.NotSupported, (await provider.AssignPatientAsync("192.168.2.101", "P-17", "X")).Status);
        Assert.Equal(0, sent);
    }

    [Fact]
    public async Task A_new_name_goes_only_to_the_monitor_that_holds_this_patient()
    {
        var (provider, sent) = await ReportedAsync(patientOnMonitor: "P-17", (_, _) => Accept);
        using var _ = provider;

        Assert.True((await provider.UpdatePatientAsync("10.10.46.48", "P-17", "SMITH ANN")).Accepted);
        Assert.Equal(MonitorAdmissionStatus.Rejected, (await provider.UpdatePatientAsync("10.10.46.48", "P-99", "DOE JOHN")).Status);

        var only = Assert.Single(sent);
        Assert.Equal(9006, only.Port);
        Assert.Contains("PID|||P-17||SMITH^ANN\r", only.Message);
    }

    [Fact]
    public async Task Disposing_stops_the_dialling()
    {
        var reader = new FakeReader(FakeReader.EndsImmediately);
        var provider = Provider(reader);
        provider.Start();
        await Wait.UntilAsync(() => reader.Attempts >= 2, "the link is being retried");

        provider.Dispose();
        await Task.Delay(50);
        int attempts = reader.Attempts;
        await Task.Delay(100);

        Assert.Equal(attempts, reader.Attempts);
    }
}
