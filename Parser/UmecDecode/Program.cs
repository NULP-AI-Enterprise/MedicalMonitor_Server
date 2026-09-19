// Decode Mindray uMec10 HL7 ORU (from TCP 4601) into readable vitals.
// C# port of decode.py — same modes, same files, same output.
//
// Three modes:
//   decode --live [ip]      LIVE dashboard: connect once, archive raw HL7 to
//                           hl7_messages/, redraw decoded vitals in real time,
//                           append changes to a CSV.
//   decode [ip]             one snapshot -> printed report + CSV
//   decode somefile.log     decode a previously saved hl7_*.log
//
// The monitor allows only ONE TCP connection at a time, so run --live INSTEAD
// of the ./read logger (this mode archives the raw stream itself).
//
// Notes:
//   * Cyrillic labels are byte-shifted +0x10 ('ЗББ' -> 'ЧСС' = HR); we un-shift.
//   * The HL7 feed carries patient info, alarm limits, and episodic NIBP. It
//     does NOT carry continuously-streaming HR/SpO2 numbers (those are
//     binary-only), so the live view refreshes whenever a value like NIBP
//     changes.
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using static UmecDecode.Hl7;
using static UmecDecode.Reports;

namespace UmecDecode;

static class Program
{
    const string MonitorIp = "192.168.0.100";
    const string OutDir = "hl7_messages";
    const string ClearScreen = "[2J[H";
    const int ChunkSize = 16384;

    // What bytes.strip() removes in the original; string.Trim() would also
    // eat Latin-1 chars like 0xA0 that are really part of a Cyrillic label.
    static readonly char[] AsciiWhitespace = { ' ', '\t', '\n', '\r', '\v', '\f' };

    static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.CancelKeyPress += (_, _) => Console.WriteLine("\nstopped.");
        try
        {
            if (args.Length > 0 && args[0] == "--live")
                RunLive(args.Length > 1 ? args[1] : MonitorIp);
            else
                RunOnce(args.Length > 0 ? args[0] : "");
            return 0;
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException)
        {
            Console.Error.WriteLine($"error: {e.Message}");
            return 1;
        }
    }

    static void RunLive(string ip)
    {
        Directory.CreateDirectory(OutDir);
        var st = new State();
        string src = $"{ip}:{Port} LIVE";
        string trend = Path.Combine(OutDir, "vitals_trend.csv");
        string latestCsv = Path.Combine(OutDir, "decoded_latest.csv");
        string latestTxt = Path.Combine(OutDir, "decoded_latest.txt");
        long lastRender = 0;
        string? lastMeasSig = null;
        var chunk = new byte[ChunkSize];

        while (true)
        {
            Socket s;
            try
            {
                s = Connect(ip, Port, TimeSpan.FromSeconds(5));
            }
            catch (Exception e)
            {
                Console.Write($"{ClearScreen} connecting to {ip} ... ({e.Message})\n");
                Thread.Sleep(2000);
                continue;
            }
            string buf = "";
            string logPath = Path.Combine(OutDir, $"hl7_{DateTime.Now:yyyy-MM-dd}.log");
            try
            {
                while (true)
                {
                    int n;
                    try
                    {
                        n = s.Receive(chunk);
                    }
                    catch (SocketException e) when (IsTimeout(e))
                    {
                        n = -1;                 // connection alive, just idle
                    }
                    if (n == 0) break;          // EOF: monitor closed -> reconnect
                    if (n > 0)
                    {
                        buf += Raw.GetString(chunk, 0, n);
                        var msgs = SplitMllp(ref buf);
                        using (var lf = new FileStream(logPath, FileMode.Append))
                        {
                            foreach (var m in msgs)
                            {
                                if (!m.Contains("MSH")) continue;
                                st.Apply(m);
                                var pretty = m.Replace('\r', '\n');
                                lf.Write(Raw.GetBytes($"----- {DateTime.Now:HH:mm:ss} -----\n{pretty}\n\n"));
                            }
                        }
                        // record a trend row whenever measured vitals change
                        var sig = MeasuredSignature(st);
                        if (sig != lastMeasSig && st.MeasuredIds().Any())
                        {
                            lastMeasSig = sig;
                            AppendTrend(st, trend);
                            WriteCsv(st, latestCsv);
                        }
                    }
                    long now = Environment.TickCount64;
                    if (now - lastRender >= 1000)
                    {
                        var frame = Render(st, src, live: true);
                        Console.Write($"{ClearScreen}{frame}\n\n (Ctrl-C to stop) trend:{trend}\n");
                        // always keep readable results on disk, updated every second
                        File.WriteAllText(latestTxt, frame + "\n");
                        WriteCsv(st, latestCsv);
                        lastRender = now;
                    }
                }
            }
            catch (Exception e) when (e is SocketException or IOException)
            {
                // dropped connection -> fall through and reconnect
            }
            finally
            {
                s.Dispose();
            }
            Thread.Sleep(1000);                 // reconnect
        }
    }

    static void RunOnce(string srcArg)
    {
        List<string> msgs;
        string src;
        if (srcArg.Length > 0 && File.Exists(srcArg))
        {
            // A saved hl7_*.log: "----- HH:MM:SS -----" headers, segments joined by \n.
            var raw = Raw.GetString(File.ReadAllBytes(srcArg));
            msgs = Regex.Split(raw, @"-----[^\n]*-----\n")
                .Select(b => b.Trim(AsciiWhitespace))
                .Where(b => b.StartsWith("MSH", StringComparison.Ordinal))
                .Select(b => b.Replace('\n', '\r'))
                .ToList();
            src = $"file {srcArg}";
        }
        else
        {
            string ip = srcArg.Length > 0 ? srcArg : MonitorIp;
            using var s = Connect(ip, Port, TimeSpan.FromSeconds(4));
            string buf = "";
            var chunk = new byte[ChunkSize];
            long t0 = Environment.TickCount64;
            while (Environment.TickCount64 - t0 < 8000)
            {
                try
                {
                    int n = s.Receive(chunk);
                    if (n == 0) break;
                    buf += Raw.GetString(chunk, 0, n);
                }
                catch (SocketException e) when (IsTimeout(e))
                {
                    break;
                }
            }
            msgs = SplitMllp(ref buf);
            src = $"{ip}:{Port}";
        }

        var st = new State();
        foreach (var m in msgs)
            if (m.Contains("MSH")) st.Apply(m);
        Console.WriteLine(Render(st, src));

        Directory.CreateDirectory(OutDir);
        var outPath = Path.Combine(OutDir, $"decoded_{DateTime.Now:yyyy-MM-dd_HHmmss}.csv");
        WriteCsv(st, outPath);
        Console.WriteLine(new string('-', 62));
        Console.WriteLine($" CSV written: {outPath}");
    }

    /// <summary>
    /// TCP connect that gives up after <paramref name="timeout"/>; the same
    /// timeout is then used for every receive (like socket.settimeout()).
    /// </summary>
    static Socket Connect(string host, int port, TimeSpan timeout)
    {
        var s = new Socket(SocketType.Stream, ProtocolType.Tcp);
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            s.ConnectAsync(host, port, cts.Token).AsTask().GetAwaiter().GetResult();
            s.ReceiveTimeout = (int)timeout.TotalMilliseconds;
            return s;
        }
        catch
        {
            s.Dispose();
            throw;
        }
    }

    static bool IsTimeout(SocketException e) =>
        e.SocketErrorCode is SocketError.TimedOut or SocketError.WouldBlock;

    /// <summary>Order-independent fingerprint of the measured values.</summary>
    static string MeasuredSignature(State st) =>
        string.Join("\n", st.Measured
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => kv.Key + "=" + kv.Value));
}
