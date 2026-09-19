// Catch Mindray uMec10 ADT (admit / discharge / transfer) separately from the
// vitals stream.
//
// The HL7 TCP stream on 4601 carries ORU^R01 only. ADT comes over a different
// channel: the monitor BROADCASTS an "online notification" ADT^A01 datagram
// once a second (Mindray Patient Data Share Protocol §6.1, §6.3.1, §6.5):
//
//     UDP 4600    (4620 when network type is DHCP and the compatibility
//                  switch is closed)
//
// Each datagram holds MSH / EVN / PID / PV1 plus OBX with the monitor name,
// standby state, bed, device id, ... Because it is UDP broadcast it can be
// captured in parallel with decode --live, which owns the monitor's single
// TCP connection.
//
// Modes:
//   adt                     listen on 4600 + 4620, dashboard, files in hl7_messages/
//   adt 4600 [4620 ...]     listen on the given UDP ports
//   adt somefile.log        replay a saved adt_*.log and print the events
//
// Files (hl7_messages/):
//   adt_YYYY-MM-DD.log      raw datagrams, archived only when their content
//                           changes (a beacon repeats identically every second)
//   adt_events.csv          ONLINE / ADMIT / DISCHARGE / UPDATE / OFFLINE rows
//   adt_latest.txt          the current dashboard frame
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using static UmecDecode.Hl7;

namespace UmecAdt;

static class Program
{
    static readonly int[] DefaultPorts = { 4600, 4620 };
    const string OutDir = "hl7_messages";
    const string ClearScreen = "\u001b[2J\u001b[H";
    const int Width = 62;
    const int RecentEvents = 12;
    static readonly TimeSpan OfflineAfter = TimeSpan.FromSeconds(10);

    static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.CancelKeyPress += (_, _) => Console.WriteLine("\nstopped.");
        try
        {
            if (args.Length == 1 && File.Exists(args[0]))
            {
                Replay(args[0]);
                return 0;
            }
            var ports = new List<int>();
            foreach (var a in args)
            {
                if (!int.TryParse(a, out var p) || p is < 1 or > 65535)
                {
                    Console.Error.WriteLine("usage: adt [udp-port ...] | adt saved_adt.log");
                    return 2;
                }
                ports.Add(p);
            }
            Listen(ports.Count > 0 ? ports.ToArray() : DefaultPorts);
            return 0;
        }
        catch (SocketException e)
        {
            Console.Error.WriteLine($"error: {e.Message}");
            return 1;
        }
    }

    static void Listen(int[] ports)
    {
        Directory.CreateDirectory(OutDir);
        var sockets = ports.Select(Bind).ToList();
        var fleet = new Fleet();
        var recent = new List<string>();
        var buf = new byte[65536];
        string src = "udp " + string.Join(", ", ports);
        string latestTxt = Path.Combine(OutDir, "adt_latest.txt");
        long lastRender = 0;

        while (true)
        {
            var ready = new List<Socket>(sockets);
            Socket.Select(ready, null, null, TimeSpan.FromSeconds(1));
            foreach (var s in ready)
            {
                EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                int n;
                try
                {
                    n = s.ReceiveFrom(buf, ref from);
                }
                catch (SocketException)
                {
                    continue;               // ICMP unreachable etc.; keep listening
                }
                var now = DateTime.Now;
                var ip = ((IPEndPoint)from).Address.ToString();
                var port = ((IPEndPoint)s.LocalEndPoint!).Port;
                var raw = Raw.GetString(buf, 0, n);
                var events = fleet.Ingest(ip, port, raw, now, out var rawChanged);
                if (rawChanged) ArchiveRaw(ip, port, raw, now);
                foreach (var ev in events) Record(ev, recent);
            }
            foreach (var ev in fleet.ExpireOffline(DateTime.Now, OfflineAfter))
                Record(ev, recent);

            long tick = Environment.TickCount64;
            if (tick - lastRender >= 1000)
            {
                var frame = Render(fleet, src, recent);
                Console.Write($"{ClearScreen}{frame}\n\n (Ctrl-C to stop)\n");
                File.WriteAllText(latestTxt, frame + "\n");
                lastRender = tick;
            }
        }
    }

    /// <summary>Feed a saved adt_*.log through the same state machine and print the events.</summary>
    static void Replay(string path)
    {
        var raw = Raw.GetString(File.ReadAllBytes(path));
        var fleet = new Fleet();
        var recent = new List<string>();
        int shown = 0;
        // header written by ArchiveRaw: "----- HH:mm:ss <ip> udp/<port> -----"
        var headers = Regex.Matches(raw, @"^----- (\d\d:\d\d:\d\d) (\S+) udp/(\d+) -----\n", RegexOptions.Multiline);
        for (int i = 0; i < headers.Count; i++)
        {
            var h = headers[i];
            int start = h.Index + h.Length;
            int end = i + 1 < headers.Count ? headers[i + 1].Index : raw.Length;
            var body = raw[start..end].Trim('\n', '\r', ' ');
            var when = DateTime.Today + TimeSpan.Parse(h.Groups[1].Value);
            var events = fleet.Ingest(h.Groups[2].Value, int.Parse(h.Groups[3].Value), body, when, out _);
            foreach (var ev in events)
            {
                Console.WriteLine(ev.ToLine());
                Remember(ev, recent);
                shown++;
            }
        }
        Console.WriteLine(new string('-', Width));
        Console.WriteLine(Render(fleet, $"file {path}", recent));
        Console.WriteLine(new string('-', Width));
        Console.WriteLine($" {headers.Count} archived datagrams, {shown} events");
    }

    static Socket Bind(int port)
    {
        var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        // let a Mindray CMS / another capture tool share the port
        s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        s.Bind(new IPEndPoint(IPAddress.Any, port));
        return s;
    }

    static void ArchiveRaw(string ip, int port, string raw, DateTime now)
    {
        var path = Path.Combine(OutDir, $"adt_{now:yyyy-MM-dd}.log");
        var pretty = raw.Trim(VT, FS, '\r', '\n').Replace('\r', '\n');
        using var lf = new FileStream(path, FileMode.Append);
        lf.Write(Raw.GetBytes($"----- {now:HH:mm:ss} {ip} udp/{port} -----\n{pretty}\n\n"));
    }

    const string EventsHeader =
        "timestamp,event,monitor_ip,monitor_name,bed,dept,patient_id,patient_name,admitted,patient_type,detail\n";

    static void Remember(AdtEvent ev, List<string> recent)
    {
        recent.Add(ev.ToLine());
        if (recent.Count > RecentEvents) recent.RemoveAt(0);
    }

    /// <summary>Keep the event on screen and append it to adt_events.csv.</summary>
    static void Record(AdtEvent ev, List<string> recent)
    {
        Remember(ev, recent);
        var path = Path.Combine(OutDir, "adt_events.csv");
        var m = ev.Monitor.Last;
        var row = string.Join(",", new[]
        {
            ev.Time.ToString("yyyy-MM-dd HH:mm:ss"), ev.Kind, ev.Monitor.Ip, m.MonitorName,
            m.BedLabel, m.Dept, m.PatientId, m.PatientName, m.AdmittedText, m.PatientTypeText, ev.Detail,
        }.Select(Csv));
        File.AppendAllText(path, (File.Exists(path) ? "" : EventsHeader) + row + "\n");
    }

    static string Csv(string s) =>
        s.IndexOfAny(new[] { ',', '"', '\n' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    static string Render(Fleet fleet, string src, List<string> recent)
    {
        var L = new List<string>();
        L.Add(new string('=', Width));
        L.Add($" Mindray uMec10 — ADT beacons   ({src})");
        L.Add($" monitors:{fleet.Monitors.Count}   datagrams:{fleet.Datagrams}   {DateTime.Now:HH:mm:ss}");
        L.Add(new string('=', Width));
        if (fleet.Monitors.Count == 0)
            L.Add(" (no ADT^A01 beacons yet — is the monitor on this subnet?)");
        foreach (var mon in fleet.Monitors.Values)
        {
            var m = mon.Last;
            var name = m.MonitorName.Length > 0 ? m.MonitorName : "unnamed";
            L.Add($" Monitor {mon.Ip}  ({name})   {(mon.Offline ? "OFFLINE" : "online")}   beacons:{mon.Count}  last:{mon.LastSeen:HH:mm:ss}");
            L.Add($"   Patient : {(m.PatientName.Length > 0 ? m.PatientName : "(none)")}   ID:{m.PatientId}");
            L.Add($"   Admitted:{m.AdmittedText}  Type:{m.PatientTypeText}  Admit date:{m.AdmitDate}  Bed:{m.BedLabel}  Dept:{m.Dept}");
            L.Add($"   Standby:{m.StandbyText}  Alarm:{m.AlarmText}  HL7 stream:{m.MonitorIp}:{m.MonitorPort}  free conns:{m.Obx.GetValueOrDefault("4524", "-")}");
            L.Add($"   Device:{m.Obx.GetValueOrDefault("4527", "-")}  type:{m.Obx.GetValueOrDefault("4528", "-")}  version:{m.Obx.GetValueOrDefault("4529", "-")}");
            L.Add(new string('-', Width));
        }
        L.Add($" EVENTS (last {RecentEvents})   -> {Path.Combine(OutDir, "adt_events.csv")}");
        if (recent.Count == 0) L.Add("   (none yet)");
        L.AddRange(recent);
        return string.Join("\n", L);
    }
}
