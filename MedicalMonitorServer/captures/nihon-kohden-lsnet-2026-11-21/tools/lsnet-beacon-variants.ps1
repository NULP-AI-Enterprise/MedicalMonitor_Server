param(
    [string]$LocalIp = '10.200.200.77',
    [string]$MonitorIp = '10.10.46.48',
    [string]$BedName = 'STATION',
    [int]$ActiveSeconds = 26,
    [int]$QuietSeconds = 12,
    [string]$Only = '',
    [int]$PreQuietSeconds = 0
)
# Tries variants of the LS-NET beacon against the monitor. For each: does the monitor still stream
# when asked (waveform packets), and does it treat us as a bed (requests it sends us while its
# Interbed window is open)? A variant with packets but no requests is a node the monitor serves
# without listing it as a bed.
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;

public static class LsNetVariants {
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

    static byte[] Request(ushort seq, byte[] target, byte[] self, byte[] payload) {
        int len = 32 + payload.Length; var b = new byte[len];
        b[2] = (byte)(seq >> 8); b[3] = (byte)seq; b[4] = (byte)(len >> 8); b[5] = (byte)len; b[7] = 1; b[9] = 1;
        b[12] = 0x02; b[13] = 0x22; b[16] = (byte)((len - 12) >> 8); b[17] = (byte)(len - 12);
        target.CopyTo(b, 24); self.CopyTo(b, 28); payload.CopyTo(b, 32);
        return b;
    }

    public static Dictionary<string, int> Seen = new Dictionary<string, int>();
    public class Variant { public string Name; public Action<byte[]> Change; }

    public static int PreQuiet = 0;
    public static List<string> Run(string localIp, string monitorIp, string bedName, int activeSeconds, int quietSeconds, string only) {
        var result = new List<string>();
        var local = IPAddress.Parse(localIp); var monitor = IPAddress.Parse(monitorIp);
        var self = local.GetAddressBytes(); var mon = monitor.GetAddressBytes();
        var monId = new byte[] { mon[1], mon[2], mon[3], 0x20 };
        var subscribe = new byte[] { 4, 0, 0, 0, 0, 0, 1, 0, 8, 0, 1, 1, 1, 0, 0, 0, 0x55, 0x55, 0x55, 0x7F, 1, 0, 0, 0, 0x55, 0x55, 0x55, 0x57 };

        var variants = new List<Variant> {
            new Variant { Name = "0 unchanged (a bed)",            Change = b => { } },
            new Variant { Name = "1 byte 33: 01 -> 02",            Change = b => { b[33] = 0x02; } },
            new Variant { Name = "2 byte 33: 01 -> 00",            Change = b => { b[33] = 0x00; } },
            new Variant { Name = "3 byte 55: 20 -> 10",            Change = b => { b[55] = 0x10; } },
            new Variant { Name = "4 byte 55: 20 -> 00",            Change = b => { b[55] = 0x00; } },
            new Variant { Name = "5 byte 55: 20 -> 40",            Change = b => { b[55] = 0x40; } },
            new Variant { Name = "6 bytes 60-62: 01 86 01 -> 0",   Change = b => { b[60] = 0; b[61] = 0; b[62] = 0; } },
            new Variant { Name = "7 byte 62: 01 -> 00",            Change = b => { b[62] = 0x00; } },
            new Variant { Name = "8 byte 62: 01 -> 02",            Change = b => { b[62] = 0x02; } },
            new Variant { Name = "9 byte 60: 01 -> 02",            Change = b => { b[60] = 0x02; } },
            new Variant { Name = "10 no bed name",                 Change = b => { for (int i = 36; i < 48; i++) b[i] = 0; } },
        };
        if (!string.IsNullOrEmpty(only)) { var keep = only.Split(',').Select(x => x.Trim()).ToArray(); variants = variants.Where(v => keep.Contains(v.Name.Split(' ')[0])).ToList(); }

        var control = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp); control.EnableBroadcast = true; control.Bind(new IPEndPoint(local, 55405));
        var in8300 = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp); in8300.EnableBroadcast = true; in8300.Bind(new IPEndPoint(IPAddress.Any, 8300));
        var in8301 = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp); in8301.Bind(new IPEndPoint(IPAddress.Any, 8301));
        var sockets = new[] { control, in8300, in8301 };
        var beaconTargets = new[] { new IPEndPoint(IPAddress.Parse("10.255.255.255"), 8300), new IPEndPoint(IPAddress.Broadcast, 8300) };
        var monitorControl = new IPEndPoint(monitor, 8300);
        var bare = new byte[] { 0x06, 0xFF, 0x00, 0x00, 0x00, 0x0C, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00 };
        var buf = new byte[65536]; ushort seq = 0x0600;

        Func<bool, int[]> pump = record => {
            // returns { wave packets, requests from the monitor, subscribe responses }
            var counts = new int[3];
            foreach (var s in sockets) {
                try {
                    while (s.Available > 0) {
                        EndPoint ep = new IPEndPoint(IPAddress.Any, 0);
                        int n = s.ReceiveFrom(buf, ref ep);
                        if (!((IPEndPoint)ep).Address.Equals(monitor)) continue;
                        if (n == 64 && buf[0] == 0x04) continue;
                        if (buf[0] == 0x02 && buf[1] == 0x0B) counts[0]++;
                        else if (buf[0] == 0x00 && n >= 32) { counts[1]++; string k = "type " + buf[12].ToString("X2") + buf[13].ToString("X2") + " to id " + buf[24].ToString("X2") + buf[25].ToString("X2") + buf[26].ToString("X2") + buf[27].ToString("X2") + " len " + n; int c0; Seen.TryGetValue(k, out c0); Seen[k] = c0 + 1; }
                        else if (buf[0] == 0x02 && n >= 14 && buf[12] == 0x02 && buf[13] == 0x23) counts[2]++;
                        else if (buf[0] == 0x05) control.SendTo(bare, monitorControl);
                    }
                } catch { }
            }
            return counts;
        };

        if (PreQuiet > 0) { var until = DateTime.UtcNow.AddSeconds(PreQuiet); int stray = 0; while (DateTime.UtcNow < until) { stray += pump(false)[1]; System.Threading.Thread.Sleep(50); } result.Add("silent for " + PreQuiet + " s first; requests from the monitor meanwhile: " + stray); Seen.Clear(); }
        foreach (var v in variants) {
            var beacon = Beacon(bedName, self); v.Change(beacon);
            var buckets = new int[activeSeconds / 15 + 1];
            int waves = 0, requests = 0, responses = 0, firstWaveAt = -1, lastWaveAt = -1;
            var start = DateTime.UtcNow; var end = start.AddSeconds(activeSeconds);
            var nextBeacon = start; var nextSubscribe = start.AddSeconds(4);     // let it hear a couple of beacons first
            while (DateTime.UtcNow < end) {
                var now = DateTime.UtcNow; int t = (int)(now - start).TotalSeconds;
                if (now >= nextBeacon) { foreach (var target in beaconTargets) { try { control.SendTo(beacon, target); } catch { } } nextBeacon = now.AddSeconds(3); }
                if (now >= nextSubscribe && (lastWaveAt < 0 || t - lastWaveAt >= 2)) { control.SendTo(Request(++seq, monId, self, subscribe), monitorControl); nextSubscribe = now.AddSeconds(1); }
                var c = pump(true);
                if (c[0] > 0) { waves += c[0]; if (firstWaveAt < 0) firstWaveAt = t; lastWaveAt = t; }
                requests += c[1]; responses += c[2]; buckets[Math.Min(t / 15, buckets.Length - 1)] += c[1];
                System.Threading.Thread.Sleep(20);
            }
            result.Add(string.Format("{0,-34} stream: {1,4} packets (from {2,2} s to {3,2} s), answers {4,2} | requests from the monitor: {5,3}  => {6}",
                v.Name, waves, firstWaveAt, lastWaveAt, responses, requests,
                waves > 20 && requests == 0 ? "SERVED, NOT A BED" : waves > 20 ? "served, listed as a bed" : requests > 0 ? "listed as a bed, no stream" : "ignored"));

            result.Add("      requests per 15 s: " + string.Join(" ", buckets));
            foreach (var kv in Seen.OrderByDescending(x => x.Value)) result.Add("      " + kv.Value + " x " + kv.Key);
            Seen.Clear();

            // Stop, go silent, and let the monitor forget this node before the next variant.
            control.SendTo(Request(++seq, monId, self, new byte[16]), monitorControl);
            var quietEnd = DateTime.UtcNow.AddSeconds(quietSeconds);
            while (DateTime.UtcNow < quietEnd) { pump(false); System.Threading.Thread.Sleep(50); }
        }
        foreach (var s in sockets) s.Close();
        return result;
    }
}
'@
[LsNetVariants]::PreQuiet = $PreQuietSeconds
[LsNetVariants]::Run($LocalIp, $MonitorIp, $BedName, $ActiveSeconds, $QuietSeconds, $Only)
