param(
    [int]$Seconds = 20,
    [string]$BedName = 'ICU-002',
    [string]$LocalIp = '10.200.200.77',
    [string]$MonitorIp = '10.10.46.48',
    [string]$WavePayload = '04 00 00 00 00 00 01 00 08 00 01 01 01 00 00 00 55 55 55 7F 01 00 00 00 55 55 55 57',
    [string]$KeepAlivePayload = '00 0E 00 07 00 01 03 01 01 FF FF 00 00 00 00 00',
    [switch]$NoBeacon,
    [switch]$NoKeepAlive,
    [string]$Log = "$PSScriptRoot\lsnet-direct.log"
)
# An LS-NET viewer talking to the monitor directly: subscribes to waveform + numerics (request
# 0222), keeps the subscription alive the way the BSM-3562 itself does towards a peer, and
# records everything the monitor sends. Ends with the monitor's own "stop" request.
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;

public static class LsNetDirect {
    static string Hex(byte[] b, int n) { var h = new StringBuilder(); for (int i = 0; i < n; i++) h.Append(b[i].ToString("X2")).Append(' '); return h.ToString(); }
    static byte[] Bytes(string hex) { return hex.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(x => Convert.ToByte(x, 16)).ToArray(); }

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

    // [kind 2][seq 2][len 2] 00 01 00 01 00 00 | [type 2] 00 00 [len-12 2] 00*6 | [id 4] [ip 4] | payload
    static byte[] Message(byte kindHi, byte kindLo, ushort seq, byte typeHi, byte typeLo, byte[] id, byte[] ip, byte[] payload) {
        int len = 32 + payload.Length; var b = new byte[len];
        b[0] = kindHi; b[1] = kindLo; b[2] = (byte)(seq >> 8); b[3] = (byte)seq; b[4] = (byte)(len >> 8); b[5] = (byte)len; b[7] = 1; b[9] = 1;
        b[12] = typeHi; b[13] = typeLo; b[16] = (byte)((len - 12) >> 8); b[17] = (byte)(len - 12);
        id.CopyTo(b, 24); ip.CopyTo(b, 28); payload.CopyTo(b, 32);
        return b;
    }

    public static void Run(int seconds, string bedName, string localIp, string monitorIp, string waveHex, string keepHex, bool sendBeacon, bool keepAlive, string logPath) {
        var local = IPAddress.Parse(localIp); var monitor = IPAddress.Parse(monitorIp);
        var self = local.GetAddressBytes(); var mon = monitor.GetAddressBytes();
        var selfId = new byte[] { self[1], self[2], self[3], 0x20 }; var monId = new byte[] { mon[1], mon[2], mon[3], 0x20 };
        var lines = new List<string>(); Action<string> log = l => lines.Add(DateTime.Now.ToString("HH:mm:ss.fff") + "  " + l);

        var udp = new List<Socket>();
        for (int p = 1; p <= 65535; p++) {
            if (p == 55405) continue;
            try { var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp); s.EnableBroadcast = true; s.Bind(new IPEndPoint(IPAddress.Any, p)); udp.Add(s); } catch { }
        }
        var control = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp); control.EnableBroadcast = true; control.Bind(new IPEndPoint(local, 55405)); udp.Add(control);
        var monitorControl = new IPEndPoint(monitor, 8300);
        var beacon = Beacon(bedName, self);
        var beaconTargets = new[] { new IPEndPoint(IPAddress.Parse("10.255.255.255"), 8300), new IPEndPoint(IPAddress.Broadcast, 8300) };
        var bare = new byte[] { 0x06, 0xFF, 0x00, 0x00, 0x00, 0x0C, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00 };

        ushort seq = 0x0200; bool subscribed = false; int lastWaveSeq = -1, wavesSinceAck = 0;
        var kinds = new Dictionary<string, int>(); var buf = new byte[65536];
        var start = DateTime.UtcNow; var end = start.AddSeconds(seconds);
        var nextBeacon = DateTime.UtcNow; var nextSubscribe = DateTime.UtcNow.AddMilliseconds(500); var nextKeep = DateTime.UtcNow.AddSeconds(5);
        bool stopping = false;

        using (var raw = new StreamWriter(logPath + ".raw.txt", false)) {
            Action<string, byte[]> send = (what, m) => { control.SendTo(m, monitorControl); raw.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + " OUT >8300 " + m.Length + " " + Hex(m, m.Length)); int c; kinds.TryGetValue("sent " + what, out c); kinds["sent " + what] = c + 1; if (c == 0) log("SENT " + what + ": " + Hex(m, m.Length)); };
            while (true) {
                var now = DateTime.UtcNow;
                if (!stopping && now >= end) {
                    stopping = true; end = now.AddSeconds(2);
                    send("0222 stop", Message(0, 0, ++seq, 0x02, 0x22, monId, self, new byte[16]));
                } else if (stopping && now >= end) break;

                if (sendBeacon && now >= nextBeacon) { foreach (var t in beaconTargets) { try { control.SendTo(beacon, t); } catch { } } nextBeacon = now.AddSeconds(5); }
                if (!stopping && !subscribed && now >= nextSubscribe) { send("0222 subscribe", Message(0, 0, ++seq, 0x02, 0x22, monId, self, Bytes(waveHex))); nextSubscribe = now.AddMilliseconds(800); }
                if (!stopping && subscribed && keepAlive && now >= nextKeep) { send("05FF/040C keep-alive", Message(5, 0xFF, 0, 0x04, 0x0C, selfId, self, Bytes(keepHex))); nextKeep = now.AddSeconds(5); }
                if (!stopping && wavesSinceAck >= 90 && lastWaveSeq >= 0) { send("05FF/020C ack", Message(5, 0xFF, 0, 0x02, 0x0C, monId, self, new byte[] { (byte)(lastWaveSeq >> 8), (byte)lastWaveSeq })); wavesSinceAck = 0; }

                foreach (var s in udp) {
                    try {
                        while (s.Available > 0) {
                            EndPoint ep = new IPEndPoint(IPAddress.Any, 0);
                            int n = s.ReceiveFrom(buf, ref ep); var from = (IPEndPoint)ep;
                            if (!from.Address.Equals(monitor)) continue;
                            if (n == 64 && buf[0] == 0x04) continue;
                            int port = ((IPEndPoint)s.LocalEndPoint).Port;
                            raw.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + " IN " + from.Port + ">" + port + " " + n + " " + Hex(buf, n));
                            string kind = buf[0].ToString("X2") + buf[1].ToString("X2") + (n >= 14 && port == 8300 ? " type=" + buf[12].ToString("X2") + buf[13].ToString("X2") : "") + " " + from.Port + ">" + port + " len=" + n;
                            int c; kinds.TryGetValue(kind, out c); kinds[kind] = c + 1;
                            if (c == 0) log("NEW " + kind + "\n   " + Hex(buf, Math.Min(n, 120)));

                            if (port == 8300 && buf[0] == 0x02 && n >= 14 && buf[12] == 0x02 && buf[13] == 0x23 && !stopping) subscribed = true;
                            if (port == 8300 && buf[0] == 0x05) { control.SendTo(bare, monitorControl); raw.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + " OUT >8300 12 " + Hex(bare, 12)); }
                            if (buf[0] == 0x02 && buf[1] == 0x0B && n >= 40) { lastWaveSeq = (buf[24] << 8) | buf[25]; wavesSinceAck++; }
                        }
                    } catch { }
                }
                System.Threading.Thread.Sleep(5);
            }
        }
        log("summary:");
        foreach (var kv in kinds.OrderByDescending(k => k.Value)) log("  " + kv.Value + " x " + kv.Key);
        foreach (var s in udp) s.Close();
        File.WriteAllLines(logPath, lines);
    }
}
'@
[LsNetDirect]::Run($Seconds, $BedName, $LocalIp, $MonitorIp, $WavePayload, $KeepAlivePayload, -not $NoBeacon, -not $NoKeepAlive, $Log)
Get-Content $Log | ForEach-Object { if ($_.Length -gt 400) { $_.Substring(0, 400) + ' ...' } else { $_ } }
