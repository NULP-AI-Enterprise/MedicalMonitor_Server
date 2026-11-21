param(
    [int]$Seconds = 300,
    [string]$BedName = 'ICU-002',
    [string]$LocalIp = '10.200.200.77',
    [string]$MonitorIp = '10.10.46.48',
    [string]$Log = "$PSScriptRoot\lsnet-mirror.log"
)
# A fake LS-NET bed that answers the real monitor with the monitor's own answers. Every request
# the monitor sends to the fake bed is passed back to the monitor re-addressed; its reply and
# any data stream it starts are returned to it with the addresses rewritten, so on its screen
# the fake bed looks like a live copy of itself. Every new kind of datagram is logged in full.
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;

public static class LsNetMirror {
    static string Hex(byte[] b, int n) { var h = new StringBuilder(); for (int i = 0; i < n; i++) h.Append(b[i].ToString("X2")).Append(' '); return h.ToString(); }

    static byte[] Beacon(string bedName, byte[] a) {
        var b = new byte[64];
        new byte[] { 0x04, 0xFF, 0x00, 0x00, 0x00, 0x40, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0x02, 0x01, 0x00, 0x00, 0x00, 0x34 }.CopyTo(b, 0);
        b[24] = a[1]; b[25] = a[2]; b[26] = a[3];
        a.CopyTo(b, 28); b[33] = 0x01;
        var name = Encoding.ASCII.GetBytes(bedName); Array.Copy(name, 0, b, 36, Math.Min(name.Length, 11));
        a.CopyTo(b, 48); b[52] = a[1]; b[53] = a[2]; b[54] = a[3]; b[55] = 0x20;
        b[60] = 0x01; b[61] = 0x86; b[62] = 0x01;
        return b;
    }

    static int Replace(byte[] data, int n, byte[] find, byte[] with) {
        int hits = 0;
        for (int i = 0; i + find.Length <= n; i++) {
            bool match = true;
            for (int k = 0; k < find.Length; k++) if (data[i + k] != find[k]) { match = false; break; }
            if (match) { Array.Copy(with, 0, data, i, with.Length); hits++; i += find.Length - 1; }
        }
        return hits;
    }

    public static void Run(int seconds, string bedName, string localIp, string monitorIp, string logPath) {
        var local = IPAddress.Parse(localIp); var monitor = IPAddress.Parse(monitorIp);
        var self = local.GetAddressBytes(); var mon = monitor.GetAddressBytes();
        var selfId = new byte[] { self[1], self[2], self[3], 0x20 }; var monId = new byte[] { mon[1], mon[2], mon[3], 0x20 };

        using (var logFile = new StreamWriter(logPath, false) { AutoFlush = true })
        using (var raw = new StreamWriter(logPath + ".raw.txt", false) { AutoFlush = true }) {
            Action<string> log = line => logFile.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + "  " + line);

            const int controlSource = 55405, dataSource = 40394;
            var udp = new List<Socket>();
            for (int p = 1; p <= 65535; p++) {
                if (p == controlSource || p == dataSource) continue;
                try { var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp); s.EnableBroadcast = true; s.Bind(new IPEndPoint(IPAddress.Any, p)); udp.Add(s); } catch { }
            }
            var control = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp); control.EnableBroadcast = true; control.Bind(new IPEndPoint(local, controlSource)); udp.Add(control);
            var data = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp); data.Bind(new IPEndPoint(local, dataSource)); udp.Add(data);

            var beacon = Beacon(bedName, self);
            var beaconTargets = new[] { new IPEndPoint(IPAddress.Parse("10.255.255.255"), 8300), new IPEndPoint(IPAddress.Broadcast, 8300) };
            var monitorControl = new IPEndPoint(monitor, 8300);

            var pending = new Dictionary<ushort, ushort>();       // my sequence -> the monitor's sequence it stands for
            var kinds = new Dictionary<string, int>();
            ushort seq = 0x4000;
            var buf = new byte[65536];
            var end = DateTime.UtcNow.AddSeconds(seconds); var nextBeacon = DateTime.UtcNow;
            log("mirror up as '" + bedName + "' (" + localIp + "), listening on " + udp.Count + " UDP ports");

            while (DateTime.UtcNow < end) {
                if (DateTime.UtcNow >= nextBeacon) { foreach (var t in beaconTargets) { try { control.SendTo(beacon, t); } catch { } } nextBeacon = DateTime.UtcNow.AddSeconds(5); }

                foreach (var s in udp) {
                    try {
                        while (s.Available > 0) {
                            EndPoint ep = new IPEndPoint(IPAddress.Any, 0);
                            int n = s.ReceiveFrom(buf, ref ep);
                            var from = (IPEndPoint)ep;
                            if (!from.Address.Equals(monitor)) continue;
                            int port = ((IPEndPoint)s.LocalEndPoint).Port;
                            if (n == 64 && buf[0] == 0x04 && buf[1] == 0xFF) continue;                  // the monitor's beacon
                            raw.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + " IN " + from.Port + ">" + port + " " + n + " " + Hex(buf, n));

                            string what; byte[] outgoing = new byte[n]; Array.Copy(buf, outgoing, n); IPEndPoint to; Socket via;
                            if (port == 8300 && n >= 32 && buf[0] == 0x00) {
                                // A request from the monitor to the fake bed: ask the monitor the same thing.
                                ushort theirs = (ushort)((buf[2] << 8) | buf[3]); ushort mine = ++seq;
                                pending[mine] = theirs;
                                outgoing[2] = (byte)(mine >> 8); outgoing[3] = (byte)mine;
                                Replace(outgoing, n, selfId, monId);                                       // target: the monitor itself
                                Array.Copy(self, 0, outgoing, 28, 4);                                      // sender: us
                                what = "REQUEST  " + buf[0].ToString("X2") + buf[1].ToString("X2") + " type=" + buf[12].ToString("X2") + buf[13].ToString("X2") + " len=" + n;
                                to = monitorControl; via = control;
                            } else if (port == 8300 && n >= 32 && buf[0] == 0x02) {
                                // The monitor's answer to a mirrored request: hand it back as the fake bed's answer.
                                ushort mine = (ushort)((buf[2] << 8) | buf[3]); ushort theirs;
                                if (!pending.TryGetValue(mine, out theirs)) continue;
                                pending.Remove(mine);
                                outgoing[2] = (byte)(theirs >> 8); outgoing[3] = (byte)theirs;
                                Replace(outgoing, n, monId, selfId); Replace(outgoing, n, mon, self);
                                what = "RESPONSE " + buf[0].ToString("X2") + buf[1].ToString("X2") + " type=" + buf[12].ToString("X2") + buf[13].ToString("X2") + " len=" + n;
                                to = monitorControl; via = control;
                            } else {
                                // A data stream the monitor sends to its viewer: return it as the fake bed's stream.
                                Replace(outgoing, n, monId, selfId); Replace(outgoing, n, mon, self);
                                what = "STREAM   " + buf[0].ToString("X2") + buf[1].ToString("X2") + " " + from.Port + ">" + port + " len=" + n;
                                to = new IPEndPoint(monitor, port); via = data;
                            }

                            int count; kinds.TryGetValue(what, out count); kinds[what] = count + 1;
                            if (count == 0) log("NEW " + what + "\n   " + Hex(buf, Math.Min(n, 700)));
                            try { via.SendTo(outgoing, to); raw.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + " OUT >" + to.Port + " " + n + " " + Hex(outgoing, n)); } catch (Exception ex) { log("send failed: " + ex.Message); }
                        }
                    } catch { }
                }
                System.Threading.Thread.Sleep(5);
            }
            log("done. kinds seen:");
            foreach (var kv in kinds.OrderByDescending(k => k.Value)) log("  " + kv.Value + " x " + kv.Key);
            foreach (var s in udp) s.Close();
        }
    }
}
'@
[LsNetMirror]::Run($Seconds, $BedName, $LocalIp, $MonitorIp, $Log)
