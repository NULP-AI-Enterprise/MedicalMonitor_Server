param(
    [int]$Seconds = 40,
    [string]$BedName = 'ICU-002',
    [string]$LocalIp = '10.200.200.77',
    [string]$MonitorIp = '10.10.46.48',
    [int]$BeaconEverySeconds = 5,
    [string]$Log = "$PSScriptRoot\lsnet-fakebed.log",
    [switch]$NoBeacon
)
# Pretends to be another Nihon Kohden bed on LS-NET: broadcasts a beacon modelled on the real
# monitor's, and logs every UDP datagram and every TCP connection (with what it sends) that
# reaches this PC on any port. Nothing is ever sent back on TCP.
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

public static class LsNetFakeBed {
    static string Hex(byte[] b, int n) {
        var hex = new StringBuilder(); var asc = new StringBuilder();
        for (int i = 0; i < n; i++) { hex.Append(b[i].ToString("X2")).Append(' '); asc.Append(b[i] >= 32 && b[i] < 127 ? (char)b[i] : '.'); }
        return hex + "\n   " + asc;
    }

    public static byte[] Beacon(string bedName, IPAddress ip) {
        var a = ip.GetAddressBytes();
        var b = new byte[64];
        // Header and record framing exactly as the BSM-3562 sends them.
        new byte[] { 0x04, 0xFF, 0x00, 0x00, 0x00, 0x40, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0x02, 0x01, 0x00, 0x00, 0x00, 0x34 }.CopyTo(b, 0);
        b[24] = a[1]; b[25] = a[2]; b[26] = a[3];
        a.CopyTo(b, 28);
        b[33] = 0x01;
        var name = Encoding.ASCII.GetBytes(bedName);
        Array.Copy(name, 0, b, 36, Math.Min(name.Length, 11));
        a.CopyTo(b, 48);
        b[52] = a[1]; b[53] = a[2]; b[54] = a[3]; b[55] = 0x20;
        b[60] = 0x01; b[61] = 0x86; b[62] = 0x01;
        return b;
    }

    public static void Run(int seconds, string bedName, string localIp, string monitorIp, int beaconEvery, string logPath, bool sendBeacon) {
        Action<string> log = line => {
            var text = DateTime.Now.ToString("HH:mm:ss.fff") + "  " + line;
            File.AppendAllText(logPath, text + "\n");
        };
        var local = IPAddress.Parse(localIp);
        var monitor = IPAddress.Parse(monitorIp);
        const int beaconSourcePort = 55405;

        var udp = new List<Socket>(); var tcp = new List<Socket>();
        int udpSkipped = 0, tcpSkipped = 0;
        for (int p = 1; p <= 65535; p++) {
            if (p != beaconSourcePort) {
                try { var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp); s.EnableBroadcast = true; s.Bind(new IPEndPoint(IPAddress.Any, p)); udp.Add(s); } catch { udpSkipped++; }
            }
            try { var l = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp); l.Bind(new IPEndPoint(local, p)); l.Listen(4); tcp.Add(l); } catch { tcpSkipped++; }
        }
        var sender = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        sender.EnableBroadcast = true;
        sender.Bind(new IPEndPoint(local, beaconSourcePort));
        udp.Add(sender);
        log("listening: UDP " + udp.Count + " ports (skipped " + udpSkipped + "), TCP " + tcp.Count + " ports on " + localIp + " (skipped " + tcpSkipped + "); beacon " + (sendBeacon ? "'" + bedName + "' every " + beaconEvery + " s" : "OFF"));

        var beacon = Beacon(bedName, local);
        if (sendBeacon) log("beacon bytes:\n   " + Hex(beacon, beacon.Length));
        var targets = new[] { new IPEndPoint(IPAddress.Parse("10.255.255.255"), 8300), new IPEndPoint(IPAddress.Broadcast, 8300) };

        var open = new List<Socket>();
        var buf = new byte[65536];
        var start = DateTime.UtcNow; var end = start.AddSeconds(seconds);
        var nextBeacon = DateTime.UtcNow;
        int monitorBeacons = 0, sent = 0;
        string lastMonitorBeacon = null;

        while (DateTime.UtcNow < end) {
            if (sendBeacon && DateTime.UtcNow >= nextBeacon) {
                foreach (var t in targets) { try { sender.SendTo(beacon, t); } catch (Exception ex) { log("beacon send to " + t + " failed: " + ex.Message); } }
                sent++; nextBeacon = DateTime.UtcNow.AddSeconds(beaconEvery);
            }
            foreach (var s in udp) {
                try {
                    while (s.Available > 0) {
                        EndPoint ep = new IPEndPoint(IPAddress.Any, 0);
                        int n = s.ReceiveFrom(buf, ref ep);
                        var from = (IPEndPoint)ep;
                        if (from.Address.Equals(local)) continue;                 // our own broadcast coming back
                        int port = ((IPEndPoint)s.LocalEndPoint).Port;
                        var hex = Hex(buf, Math.Min(n, 512));
                        if (port == 8300 && n == 64 && from.Address.Equals(monitor)) {
                            monitorBeacons++;
                            if (hex == lastMonitorBeacon) continue;               // the usual beacon, unchanged
                            lastMonitorBeacon = hex;
                            log("UDP beacon from the monitor (new or changed), " + from + " -> port 8300, len 64\n   " + hex);
                            continue;
                        }
                        log("UDP from " + from + " -> local port " + port + ", len " + n + "\n   " + hex);
                    }
                } catch { }
            }
            foreach (var l in tcp) {
                try {
                    if (!l.Poll(0, SelectMode.SelectRead)) continue;
                    var c = l.Accept();
                    log("TCP CONNECT from " + c.RemoteEndPoint + " -> local port " + ((IPEndPoint)l.LocalEndPoint).Port);
                    open.Add(c);
                } catch { }
            }
            for (int i = open.Count - 1; i >= 0; i--) {
                var c = open[i];
                try {
                    if (!c.Poll(0, SelectMode.SelectRead)) continue;
                    int n = c.Receive(buf);
                    if (n == 0) { log("TCP closed by " + c.RemoteEndPoint + " (local port " + ((IPEndPoint)c.LocalEndPoint).Port + ")"); c.Close(); open.RemoveAt(i); continue; }
                    log("TCP DATA from " + c.RemoteEndPoint + " -> local port " + ((IPEndPoint)c.LocalEndPoint).Port + ", len " + n + "\n   " + Hex(buf, Math.Min(n, 1024)));
                } catch (Exception ex) { log("TCP error: " + ex.Message); try { c.Close(); } catch { } open.RemoveAt(i); }
            }
            System.Threading.Thread.Sleep(50);
        }
        log("done: " + sent + " beacons sent, " + monitorBeacons + " monitor beacons seen");
        foreach (var s in udp) s.Close(); foreach (var l in tcp) l.Close(); foreach (var c in open) c.Close();
    }
}
'@
[LsNetFakeBed]::Run($Seconds, $BedName, $LocalIp, $MonitorIp, $BeaconEverySeconds, $Log, -not $NoBeacon)
Get-Content $Log
