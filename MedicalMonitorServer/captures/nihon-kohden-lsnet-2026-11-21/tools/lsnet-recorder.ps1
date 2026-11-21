param(
    [int]$Seconds = 1200,
    [string]$LocalIp = '10.200.200.77',
    [string]$MonitorIp = '10.10.46.48',
    [string]$Out = "$PSScriptRoot\lsnet-alarms.raw.txt"
)
# Records everything a BSM-3562 says over LS-NET while somebody provokes alarms on it: the
# waveform + numerics stream, and the answers to the requests the monitor itself sends a peer it
# is watching (020F, 031A, 0308), asked every two seconds. Announces itself as a plain node.
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

public static class LsNetRecorder {
    static string Hex(byte[] b, int n) { var h = new StringBuilder(n * 3); for (int i = 0; i < n; i++) h.Append(b[i].ToString("X2")).Append(' '); return h.ToString(); }

    static byte[] Message(byte kindLow, ushort seq, byte typeHi, byte typeLo, byte[] id, byte[] ip, byte[] payload) {
        int len = 32 + payload.Length; var b = new byte[len];
        b[1] = kindLow; b[2] = (byte)(seq >> 8); b[3] = (byte)seq; b[4] = (byte)(len >> 8); b[5] = (byte)len; b[7] = 1; b[9] = 1;
        b[12] = typeHi; b[13] = typeLo; b[16] = (byte)((len - 12) >> 8); b[17] = (byte)(len - 12);
        id.CopyTo(b, 24); ip.CopyTo(b, 28); payload.CopyTo(b, 32);
        return b;
    }

    public static void Run(int seconds, string localIp, string monitorIp, string path) {
        var local = IPAddress.Parse(localIp); var monitor = IPAddress.Parse(monitorIp);
        var a = local.GetAddressBytes(); var m = monitor.GetAddressBytes();
        var monId = new byte[] { m[1], m[2], m[3], 0x20 };

        var beacon = new byte[64];
        new byte[] { 0x04, 0xFF, 0x00, 0x00, 0x00, 0x40, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0x02, 0x01, 0x00, 0x00, 0x00, 0x34 }.CopyTo(beacon, 0);
        beacon[24] = a[1]; beacon[25] = a[2]; beacon[26] = a[3]; a.CopyTo(beacon, 28); beacon[33] = 0x01;
        Encoding.ASCII.GetBytes("RECORDER").CopyTo(beacon, 36);
        a.CopyTo(beacon, 48); beacon[52] = a[1]; beacon[53] = a[2]; beacon[54] = a[3]; beacon[55] = 0x00;      // a node, not a bed
        beacon[60] = 0x01; beacon[61] = 0x86; beacon[62] = 0x01;

        var subscribe = new byte[] { 4, 0, 0, 0, 0, 0, 1, 0, 8, 0, 1, 1, 1, 0, 0, 0, 0x55, 0x55, 0x55, 0x7F, 1, 0, 0, 0, 0x55, 0x55, 0x55, 0x57 };
        var bare = new byte[] { 0x06, 0xFF, 0x00, 0x00, 0x00, 0x0C, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00 };

        var control = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp); control.EnableBroadcast = true; control.Bind(new IPEndPoint(local, 55405));
        var in8300 = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp); in8300.EnableBroadcast = true; in8300.Bind(new IPEndPoint(IPAddress.Any, 8300));
        var in8301 = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp); in8301.Bind(new IPEndPoint(IPAddress.Any, 8301));
        var sockets = new[] { control, in8300, in8301 };
        var targets = new[] { new IPEndPoint(IPAddress.Parse("10.255.255.255"), 8300), new IPEndPoint(IPAddress.Broadcast, 8300) };
        var monitorControl = new IPEndPoint(monitor, 8300);

        var buf = new byte[65536]; ushort seq = 0x0700;
        var end = DateTime.UtcNow.AddSeconds(seconds);
        var nextBeacon = DateTime.UtcNow; var nextSubscribe = DateTime.UtcNow.AddSeconds(3); var nextPoll = DateTime.UtcNow.AddSeconds(5);
        var lastStream = DateTime.MinValue;

        using (var raw = new StreamWriter(path, false) { AutoFlush = true }) {
            while (DateTime.UtcNow < end) {
                var now = DateTime.UtcNow;
                if (now >= nextBeacon) { foreach (var t in targets) { try { control.SendTo(beacon, t); } catch { } } nextBeacon = now.AddSeconds(3); }
                if (now >= nextSubscribe && (now - lastStream).TotalSeconds > 2) { control.SendTo(Message(0, ++seq, 0x02, 0x22, monId, a, subscribe), monitorControl); nextSubscribe = now.AddSeconds(1); }
                if (now >= nextPoll) {
                    control.SendTo(Message(0, ++seq, 0x02, 0x0F, monId, a, new byte[] { 1, 0, 0, 0 }), monitorControl);
                    control.SendTo(Message(0, ++seq, 0x03, 0x1A, monId, a, new byte[0]), monitorControl);
                    control.SendTo(Message(1, ++seq, 0x03, 0x08, monId, a, new byte[0]), monitorControl);
                    nextPoll = now.AddSeconds(2);
                }
                foreach (var s in sockets) {
                    try {
                        while (s.Available > 0) {
                            EndPoint ep = new IPEndPoint(IPAddress.Any, 0);
                            int n = s.ReceiveFrom(buf, ref ep); var from = (IPEndPoint)ep;
                            if (!from.Address.Equals(monitor)) continue;
                            if (n == 64 && buf[0] == 0x04) continue;
                            int port = ((IPEndPoint)s.LocalEndPoint).Port;
                            if (buf[0] == 0x02 && (buf[1] == 0x0B || buf[1] == 0x12)) lastStream = DateTime.UtcNow;
                            if (buf[0] == 0x05) control.SendTo(bare, monitorControl);
                            raw.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + " IN " + from.Port + ">" + port + " " + n + " " + Hex(buf, n));
                        }
                    } catch { }
                }
                System.Threading.Thread.Sleep(10);
            }
            control.SendTo(Message(0, ++seq, 0x02, 0x22, monId, a, new byte[16]), monitorControl);
        }
        foreach (var s in sockets) s.Close();
    }
}
'@
[LsNetRecorder]::Run($Seconds, $LocalIp, $MonitorIp, $Out)
"recorded to $Out"
