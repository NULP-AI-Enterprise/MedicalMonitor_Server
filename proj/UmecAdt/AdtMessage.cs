using static UmecDecode.Hl7;

namespace UmecAdt;

/// <summary>
/// One decoded ADT datagram from a bedside monitor (Mindray PDS protocol §6.5,
/// "Online Notification Message of the Patient Monitor (ADT^A01)").
/// </summary>
sealed class AdtMessage
{
    public string Type = "";            // MSH-9, e.g. ADT^A01
    public string ControlId = "";       // MSH-10
    public string AdmitDate = "";       // EVN-2, yyyyMMdd ("00000000" = not admitted)
    public string PatientId = "";       // PID-3, the monitor's GUID for the patient
    public string PatientName = "";     // PID-5 <First>^<Last>, de-shifted
    public string Dept = "";            // PV1-3 bed subcomponents:
    public string Bed = "";             //   <dept>&<bed>&<ip uint32>&<port>&<iid>&<admitted>
    public string MonitorIp = "";
    public string MonitorPort = "";
    public string PatientIid = "";
    public bool? Admitted;
    public string PatientType = "";     // PV1-18: N/A/P/U
    public readonly Dictionary<string, string> Obx = new();   // code -> first component

    /// <summary>OBX codes carried by the beacon (PDS guide appendix B.4).</summary>
    public static readonly Dictionary<string, string> ObxNames = new()
    {
        ["2304"] = "Monitor name",
        ["2305"] = "Standby",
        ["2307"] = "Highest alarm type",
        ["2211"] = "IP sequence",
        ["4524"] = "Free connections",
        ["4526"] = "Telemetry serial",
        ["2308"] = "Bed No (string)",
        ["4527"] = "Device id",
        ["4528"] = "Machine type",
        ["4529"] = "Machine version",
        ["4530"] = "Highest alarm confirmed",
        ["2319"] = "ViewBed device id",
        ["2320"] = "ViewBed id length",
    };

    static readonly Dictionary<string, string> PatientTypes = new()
    {
        ["N"] = "Neonate", ["A"] = "Adult", ["P"] = "Pediatric", ["U"] = "Unknown",
    };
    static readonly Dictionary<string, string> AlarmTypes = new()
    {
        ["0"] = "none", ["1"] = "physiological", ["2"] = "technical",
    };

    public string MonitorName => Obx.GetValueOrDefault("2304", "");
    public string BedNoStr => Obx.GetValueOrDefault("2308", "");
    public string BedLabel => Bed.Length > 0 ? Bed : BedNoStr;
    public string StandbyText => Obx.GetValueOrDefault("2305", "") == "1" ? "standby" : "no";
    public string AlarmText => AlarmTypes.GetValueOrDefault(Obx.GetValueOrDefault("2307", ""), Obx.GetValueOrDefault("2307", "-"));
    public string PatientTypeText => PatientTypes.GetValueOrDefault(PatientType, PatientType);
    public string AdmittedText => Admitted switch { true => "yes", false => "no", null => "?" };

    /// <summary>
    /// Parse one datagram. Tolerates MLLP framing and LF-separated segments.
    /// Returns null when there is no MSH segment.
    /// </summary>
    public static AdtMessage? Parse(string raw)
    {
        var m = new AdtMessage();
        var msg = raw.Trim(VT, FS, '\r', '\n', '\0', ' ');
        foreach (var seg in msg.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var f = seg.Split('|');
            switch (f[0])
            {
                case "MSH":                         // MSH-1 is the '|' itself, so MSH-n = f[n-1]
                    m.Type = Field(f, 8);
                    m.ControlId = Field(f, 9);
                    break;
                case "EVN":
                    m.AdmitDate = Field(f, 2);
                    break;
                case "PID":
                    m.PatientId = Field(f, 3);
                    m.PatientName = Deshift(string.Join(" ", Field(f, 5).Split('^').Where(x => x.Length > 0))).Trim();
                    break;
                case "PV1":
                    ParseLocation(m, Field(f, 3));
                    m.PatientType = Field(f, 18);
                    break;
                case "OBX":
                {
                    var code = Field(f, 3).Split('^')[0];
                    if (code.Length == 0) break;
                    var value = Field(f, 5);
                    if (Field(f, 2) != "ST") value = value.Split('^')[0];   // CE/NM: "<code>^<text>"
                    m.Obx[code] = Deshift(value);
                    break;
                }
            }
        }
        return m.Type.Length > 0 ? m : null;
    }

    // PV1-3 = <point of care>^<room>^<bed>; only <bed> is used and it is
    // <dept>&<bed id>&<ip as uint32>&<tcp port>&<patient iid>&<admitted flag>.
    // Some firmwares drop the empty <patient iid>, leaving five subcomponents.
    static void ParseLocation(AdtMessage m, string pv13)
    {
        var comp = pv13.Split('^');
        var sc = (comp.Length > 2 ? comp[2] : "").Split('&');
        m.Dept = Deshift(Sub(sc, 0));
        m.Bed = Sub(sc, 1);
        m.MonitorIp = IpFromUint(Sub(sc, 2));
        m.MonitorPort = Sub(sc, 3);
        if (sc.Length >= 6)
        {
            m.PatientIid = sc[4];
            m.Admitted = Flag(sc[5]);
        }
        else if (sc.Length == 5)
        {
            m.Admitted = Flag(sc[4]);
        }
    }

    static string Field(string[] f, int i) => i < f.Length ? f[i] : "";
    static string Sub(string[] sc, int i) => i < sc.Length ? sc[i] : "";
    static bool? Flag(string s) => s switch { "1" => true, "0" => false, _ => null };

    /// <summary>"3232235620" -> "192.168.0.100" (network byte order); anything else is returned as-is.</summary>
    static string IpFromUint(string s) =>
        uint.TryParse(s, out var n) ? $"{n >> 24}.{(n >> 16) & 255}.{(n >> 8) & 255}.{n & 255}" : s;
}
