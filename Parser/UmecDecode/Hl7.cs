using System.Text;

namespace UmecDecode;

/// <summary>
/// Mindray uMec10 HL7 ORU specifics: parameter tables, the Cyrillic byte
/// shift, MLLP framing.
///
/// Raw bytes travel through the program as Latin-1 strings (one char per
/// byte, lossless both ways), so segments/fields can be split with plain
/// string operations exactly like the Python original did on bytes.
/// </summary>
static class Hl7
{
    public const int Port = 4601;

    public const char VT = (char)0x0B;   // MLLP start block
    public const char FS = (char)0x1C;   // MLLP end block

    /// <summary>Byte-transparent encoding used to carry raw HL7 in strings.</summary>
    public static readonly Encoding Raw = Encoding.Latin1;
    static readonly Encoding Cp1251;

    static Hl7()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Cp1251 = Encoding.GetEncoding(1251,
            EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
    }

    /// <summary>Parameter code -> (display name, unit).</summary>
    public static readonly Dictionary<string, (string Name, string Unit)> Param = new()
    {
        ["101"] = ("Heart Rate (HR)", "bpm"),
        ["102"] = ("PVCs", "/min"),
        ["105"] = ("ST-I", "mV"), ["106"] = ("ST-II", "mV"), ["107"] = ("ST-III", "mV"),
        ["108"] = ("ST-aVR", "mV"), ["109"] = ("ST-aVL", "mV"), ["110"] = ("ST-aVF", "mV"),
        ["117"] = ("ST-V", "mV"),
        ["151"] = ("Respiration Rate (RR)", "rpm"),
        ["160"] = ("SpO2", "%"),
        ["161"] = ("Pulse Rate (PR)", "bpm"),
        ["162"] = ("Perfusion Index (PI)", "%"),
        ["170"] = ("NIBP Systolic", "mmHg"),
        ["171"] = ("NIBP Diastolic", "mmHg"),
        ["172"] = ("NIBP Mean", "mmHg"),
        ["200"] = ("Temperature T1", "°C"),
        ["201"] = ("Temperature T2", "°C"),
        ["202"] = ("Temperature TD", "°C"),
    };

    public static readonly Dictionary<string, string> Level = new()
    {
        ["0"] = "high", ["1"] = "med", ["2"] = "low", ["3"] = "off",
    };

    // Mindray "no valid measurement" sentinels.
    static readonly HashSet<string> Invalid = new() { "-100", "-1000", "-10000" };

    public static bool IsValid(string v) => !Invalid.Contains(v);

    public static string FmtMeas(string v) => IsValid(v) ? v : "—";

    /// <summary>
    /// Cyrillic labels arrive byte-shifted by +0x10 ('ЗББ' -> 'ЧСС' = HR);
    /// un-shift and decode as Windows-1251.
    /// </summary>
    public static string Deshift(string raw)
    {
        var bytes = Raw.GetBytes(raw);
        for (int i = 0; i < bytes.Length; i++)
            if (bytes[i] >= 0x80 && bytes[i] <= 0xEF)
                bytes[i] += 0x10;
        return Cp1251.GetString(bytes);
    }

    public static bool IsDigits(string s) => s.Length > 0 && s.All(char.IsAsciiDigit);

    /// <summary>Numeric ordering for parameter ids; non-numeric ids go last.</summary>
    public static int SortKey(string pid) =>
        IsDigits(pid) && int.TryParse(pid, out var n) ? n : 9999;

    /// <summary>
    /// Cut complete MLLP frames (&lt;VT&gt; message &lt;FS&gt;) out of <paramref name="buf"/>.
    /// Returns the messages; <paramref name="buf"/> keeps the unconsumed tail.
    /// </summary>
    public static List<string> SplitMllp(ref string buf)
    {
        var msgs = new List<string>();
        int i = 0;
        while (true)
        {
            int a = buf.IndexOf(VT, i);
            if (a < 0) break;
            int b = buf.IndexOf(FS, a);
            if (b < 0) break;
            msgs.Add(buf[(a + 1)..b]);
            i = b + 1;
        }
        buf = buf[i..];
        return msgs;
    }
}
