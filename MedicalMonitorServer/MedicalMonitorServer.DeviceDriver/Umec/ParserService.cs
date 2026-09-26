using System.Globalization;
using System.Text;
using MedicalMonitorServer.Contracts.Models;

namespace MedicalMonitorServer.DeviceDriver.Umec;

/// <summary>
/// Decodes one HL7 message from a Mindray uMec10 into a <see cref="UmecMessage"/>:
/// ORU^R01 from the TCP stream (port 4601) and ADT^A01 beacons from UDP 4600/4620.
/// Stateless: nothing is kept between calls. Cutting the TCP stream into
/// messages (MLLP framing) is the reader's job, see <see cref="ComunicationService"/>.
/// </summary>
public interface IParserService
{
    /// <summary>
    /// Parse one complete message. MLLP framing bytes around it are ignored.
    /// Returns null when the bytes are not an HL7 message.
    /// </summary>
    UmecMessage? Parse(ReadOnlySpan<byte> message);

    /// <summary>
    /// Same, for a message carried as a Latin-1 string (one char per byte):
    /// the form of <see cref="UmecMessage.Raw"/> and of the hl7_*.log capture files.
    /// </summary>
    UmecMessage? Parse(string message);
}

public class ParserService : IParserService
{
    private const char VT = (char)0x0B;   // MLLP start block
    private const char FS = (char)0x1C;   // MLLP end block

    /// <summary>Byte-transparent encoding: lets fields be split before the text encoding is known.</summary>
    private static readonly Encoding Latin1 = Encoding.Latin1;
    private static readonly Encoding Cp1251;

    static ParserService()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Cp1251 = Encoding.GetEncoding(1251,
            EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
    }

    public UmecMessage? Parse(ReadOnlySpan<byte> message) => Parse(Latin1.GetString(message));

    public UmecMessage? Parse(string message)
    {
        var segments = message
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim(VT, FS, '\0'))
            .Where(line => line.Length >= 3)
            .Select(line => new Segment(line))
            .ToList();
        var msh = segments.FirstOrDefault(s => s.Name == "MSH");
        if (msh == null) return null;

        var msg = new UmecMessage
        {
            MessageType = msh[9],
            ControlId = msh[10],
            MessageId = ParseInt(msh[10]) ?? 0,
            Hl7Version = msh[12],
            ReceivedAt = DateTime.Now,
            Raw = message.Trim(VT, FS, '\r', '\n', '\0'),
        };

        var pid = segments.FirstOrDefault(s => s.Name == "PID");
        var pv1 = segments.FirstOrDefault(s => s.Name == "PV1");
        var evn = segments.FirstOrDefault(s => s.Name == "EVN");
        if (pid != null || pv1 != null) msg.Patient = ParsePatient(pid, pv1);
        if (evn != null) (msg.Patient ??= new PatientInfo()).AdmitDate = ParseDate(evn[2]);

        var settings = new Dictionary<int, AlarmSetting>();
        foreach (var seg in segments.Where(s => s.Name == "OBX"))
        {
            var o = ToObservation(seg);
            msg.Observations.Add(o);
            Apply(msg, o, settings);
        }
        msg.AlarmSettings.AddRange(settings.Values);

        msg.Kind = KindOf(msg);
        return msg;
    }

    // ------------------------------------------------------------------
    // OBX interpretation. Every OBX is handled by its code (OBX-3), so the
    // result does not depend on which message id the monitor put it in.
    // ------------------------------------------------------------------

    private static UmecObservation ToObservation(Segment obx)
    {
        string id = obx[3];
        string value = obx[5];
        bool hasText = value.Contains('^');
        return new UmecObservation
        {
            ValueType = obx[2],
            Code = Component(id, 0),
            Label = DecodeText(Component(id, 1)),
            SubId = obx[4],
            Value = DecodeText(value),
            ValueCode = Component(value, 0),
            ValueText = DecodeText(Component(value, hasText ? 1 : 0)),
            Units = obx[6],
            Status = obx[11],
            AccessCheck = obx[13],
            Timestamp = ParseTimestamp(obx[14]),
        };
    }

    private static void Apply(UmecMessage msg, UmecObservation o, Dictionary<int, AlarmSetting> settings)
    {
        switch (o.Code)
        {
            // ---- parameter naming (ORU msg 11) ------------------------------
            case "2025":                        // "<pid>^<label>", OBX-4 = group
                if (ParseInt(o.ValueCode) is int lp)
                    msg.ParameterLabels.Add(new ParameterLabel
                    {
                        ParameterId = lp, Label = o.ValueText, GroupId = ParseInt(o.SubId),
                    });
                break;
            case "2023":                        // "<group>^<label>"
                if (ParseInt(o.ValueCode) is int g)
                    msg.ParameterGroups.Add(new ParameterGroup { GroupId = g, Label = o.ValueText });
                break;

            // ---- alarm setup per parameter, OBX-4 = parameter id (ORU msg 51/58/60)
            case "2002": if (Setting(o) is { } s1) s1.HighLimit = ParseNumber(o.Value); break;
            case "2003": if (Setting(o) is { } s2) s2.LowLimit = ParseNumber(o.Value); break;
            case "2004": if (Setting(o) is { } s3) s3.Enabled = ParseFlag(o.ValueCode); break;
            case "2009":
                if (Setting(o) is { } s4)
                {
                    s4.LevelCode = ParseInt(o.ValueCode);
                    s4.Level = s4.LevelCode is int c ? ToAlarmLevel(c) : null;
                }
                break;

            // ---- monitor-wide alarm state (ORU msg 53) ----------------------
            case "2013": AlarmSystem().Volume = ParseInt(o.ValueCode); break;
            case "2014": AlarmSystem().Muted = ParseFlag(o.ValueCode); break;
            case "2027": AlarmSystem().AlarmsOff = ParseFlag(o.ValueCode); break;
            case "2028": AlarmSystem().SoundPaused = ParseFlag(o.ValueCode); break;
            case "2016": AlarmSystem().AlarmPaused = ParseFlag(o.ValueCode); break;

            // ---- patient attributes (ORU msg 103, ADT beacon) ---------------
            case "51": Patient().WeightKg = ParseNumber(o.Value); break;
            case "52": Patient().HeightCm = ParseNumber(o.Value); break;
            case "2301": Patient().BedNumber = o.Value; break;
            case "2302": Patient().BloodType = o.ValueText; break;
            case "2303": Patient().Paced = ParseFlag(o.ValueCode); break;
            case "2308": Patient().BedLabel = o.Value; break;

            // ---- monitor identity and state (ADT beacon) --------------------
            case "2304": Monitor().Name = o.Value; break;
            case "2305": Monitor().Standby = ParseFlag(o.ValueCode); break;
            case "2307": Monitor().HighestAlarmType = ParseInt(o.ValueCode); break;
            case "4530": Monitor().HighestAlarmConfirmed = ParseFlag(o.ValueCode); break;
            case "4524": Monitor().FreeConnections = ParseInt(o.ValueCode); break;
            case "2211": Monitor().IpSequence = ParseInt(o.ValueCode); break;
            case "4526": Monitor().TelemetrySerial = TextOf(o); break;
            case "4527": Monitor().DeviceId = TextOf(o); break;
            case "4528": Monitor().MachineType = TextOf(o); break;
            case "4529": Monitor().MachineVersion = TextOf(o); break;
            case "2319": Monitor().ViewBedDeviceId = TextOf(o); break;
            case "2320": Monitor().ViewBedIdLength = ParseInt(o.ValueCode); break;

            // ---- active alarms (ORU msg 54/56): OBX-3 = type, OBX-4 = level, OBX-5 = "<id>^<text>"
            case "1" or "2" or "3" or "4" when o.ValueType == "CE":
                msg.Alarms.Add(ToAlarm(o));
                break;

            // ---- measurements: numeric parameter ids below 1000 (ORU msg 503)
            default:
                if (o.ValueType == "NM" && ParseInt(o.Code) is int p && p < 1000)
                    msg.Vitals.Add(ToVital(o, p));
                break;
        }

        AlarmSetting? Setting(UmecObservation obs)
        {
            if (ParseInt(obs.SubId) is not int p) return null;
            if (!settings.TryGetValue(p, out var s))
            {
                s = new AlarmSetting
                {
                    ParameterId = p,
                    ParameterName = NameOf(p, ""),
                    Unit = Parameters.TryGetValue(p, out var d) ? d.Unit : "",
                };
                settings[p] = s;
            }
            return s;
        }

        AlarmSystemStatus AlarmSystem() => msg.AlarmSystem ??= new AlarmSystemStatus();
        PatientInfo Patient() => msg.Patient ??= new PatientInfo();
        MonitorInfo Monitor() => msg.Monitor ??= new MonitorInfo();
    }

    private static VitalSign ToVital(UmecObservation o, int pid) => new()
    {
        ParameterId = pid,
        Name = NameOf(pid, o.Label),
        Label = o.Label,
        Unit = o.Units.Length > 0 ? o.Units
             : Parameters.TryGetValue(pid, out var d) ? d.Unit : "",
        Value = IsValidValue(o.Value) ? ParseNumber(o.Value) : null,
        RawValue = o.Value,
        ModuleId = ParseInt(o.SubId),
        IsAperiodic = o.AccessCheck == "APERIODIC",
        MeasuredAt = o.Timestamp,
    };

    private static ActiveAlarm ToAlarm(UmecObservation o)
    {
        int typeCode = ParseInt(o.Code) ?? 0;
        int stars = o.ValueText.Length - o.ValueText.TrimStart('*').Length;
        int? levelCode = ParseInt(o.SubId);
        return new ActiveAlarm
        {
            TypeCode = typeCode,
            Category = typeCode switch
            {
                1 => AlarmCategory.Physiological,
                3 or 4 => AlarmCategory.Technical,
                _ => AlarmCategory.Unknown,
            },
            AlarmId = ParseInt(o.ValueCode) ?? 0,
            Text = o.ValueText[stars..].Trim(),
            LevelCode = levelCode,
            // "***" high, "**" medium, "*" low; without a prefix fall back to OBX-4
            Level = stars switch
            {
                >= 3 => AlarmLevel.High,
                2 => AlarmLevel.Medium,
                1 => AlarmLevel.Low,
                _ => levelCode is int c ? ToAlarmLevel(c) : AlarmLevel.Unknown,
            },
            RaisedAt = o.Timestamp,
        };
    }

    private static PatientInfo ParsePatient(Segment? pid, Segment? pv1)
    {
        var p = new PatientInfo();
        if (pid != null)
        {
            p.PatientId = pid[3];
            string name = pid[5];
            p.LastName = DecodeText(Component(name, 0)).Trim();
            p.FirstName = DecodeText(Component(name, 1)).Trim();
            p.FullName = string.Join(" ",
                name.Split('^').Select(x => DecodeText(x).Trim()).Where(x => x.Length > 0));
            p.DateOfBirth = ParseDate(pid[7]);
            p.Sex = pid[8];
        }
        if (pv1 != null)
        {
            p.PatientClass = pv1[2];
            p.PatientType = pv1[18] switch
            {
                "A" => PatientType.Adult,
                "P" => PatientType.Pediatric,
                "N" => PatientType.Neonate,
                _ => PatientType.Unknown,
            };
            // PV1-3 = <point of care>^<room>^<bed>; <bed> is
            // <dept>&<bed>&<ip as uint32>&<port>&<patient iid>&<admitted>.
            // Some firmwares drop the empty <patient iid>, leaving five parts.
            var sc = Component(pv1[3], 2).Split('&');
            p.Department = DecodeText(Sub(sc, 0));
            p.BedNumber = Sub(sc, 1);
            p.MonitorIp = IpFromUint(Sub(sc, 2));
            p.MonitorPort = ParseInt(Sub(sc, 3));
            p.Admitted = sc.Length >= 6 ? ParseFlag(sc[5])
                       : sc.Length == 5 ? ParseFlag(sc[4]) : null;
        }
        return p;

        static string Sub(string[] parts, int i) => i < parts.Length ? parts[i] : "";
    }

    private static UmecMessageKind KindOf(UmecMessage m)
    {
        if (m.MessageType.StartsWith("ADT", StringComparison.Ordinal))
            return m.MessageType == "ADT^A01" ? UmecMessageKind.MonitorStatus : UmecMessageKind.AdtEvent;
        if (MessageKinds.TryGetValue(m.MessageId, out var kind)) return kind;

        // unknown message id: go by what it carries
        if (m.Vitals.Count > 0) return UmecMessageKind.Vitals;
        if (m.Patient != null) return UmecMessageKind.PatientInfo;
        if (m.Alarms.Count > 0)
            return m.Alarms[0].Category == AlarmCategory.Technical
                ? UmecMessageKind.TechnicalAlarms
                : UmecMessageKind.PhysiologicalAlarms;
        if (m.AlarmSettings.Count > 0)
        {
            var s = m.AlarmSettings[0];
            if (s.HighLimit != null || s.LowLimit != null) return UmecMessageKind.AlarmLimits;
            if (s.Enabled != null) return UmecMessageKind.AlarmSwitches;
            if (s.LevelCode != null) return UmecMessageKind.AlarmLevels;
        }
        if (m.ParameterLabels.Count > 0 || m.ParameterGroups.Count > 0) return UmecMessageKind.ParameterLabels;
        if (m.AlarmSystem != null) return UmecMessageKind.AlarmSystemStatus;
        if (m.Observations.Count > 0) return UmecMessageKind.Settings;
        return UmecMessageKind.Unknown;
    }

    // ------------------------------------------------------------------
    // Tables
    // ------------------------------------------------------------------

    /// <summary>Parameter id -> (name, unit).</summary>
    private static readonly Dictionary<int, (string Name, string Unit)> Parameters = new()
    {
        [101] = ("Heart Rate (HR)", "bpm"),
        [102] = ("PVCs", "/min"),
        [105] = ("ST-I", "mV"),
        [106] = ("ST-II", "mV"),
        [107] = ("ST-III", "mV"),
        [108] = ("ST-aVR", "mV"),
        [109] = ("ST-aVL", "mV"),
        [110] = ("ST-aVF", "mV"),
        [117] = ("ST-V", "mV"),
        [151] = ("Respiration Rate (RR)", "rpm"),
        [160] = ("SpO2", "%"),
        [161] = ("Pulse Rate (PR)", "bpm"),
        [162] = ("Perfusion Index (PI)", "%"),
        [170] = ("NIBP Systolic", "mmHg"),
        [171] = ("NIBP Diastolic", "mmHg"),
        [172] = ("NIBP Mean", "mmHg"),
        [200] = ("Temperature T1", "°C"),
        [201] = ("Temperature T2", "°C"),
        [202] = ("Temperature TD", "°C"),
        [600] = ("Pulse Rate (PR)", "bpm"),
    };

    /// <summary>ORU message id (MSH-10) -> what the message carries.</summary>
    private static readonly Dictionary<int, UmecMessageKind> MessageKinds = new()
    {
        [103] = UmecMessageKind.PatientInfo,
        [11] = UmecMessageKind.ParameterLabels,
        [1202] = UmecMessageKind.ParameterLabels,
        [51] = UmecMessageKind.AlarmLimits,
        [60] = UmecMessageKind.AlarmSwitches,
        [58] = UmecMessageKind.AlarmLevels,
        [53] = UmecMessageKind.AlarmSystemStatus,
        [54] = UmecMessageKind.PhysiologicalAlarms,
        [56] = UmecMessageKind.TechnicalAlarms,
        [503] = UmecMessageKind.Vitals,
        [5] = UmecMessageKind.Settings,
        [12] = UmecMessageKind.Settings,
        [159] = UmecMessageKind.Settings,
        [160] = UmecMessageKind.Settings,
        [161] = UmecMessageKind.Settings,
        [251] = UmecMessageKind.Settings,
        [253] = UmecMessageKind.Settings,
        [256] = UmecMessageKind.Settings,
        [301] = UmecMessageKind.Settings,
        [320] = UmecMessageKind.Settings,
        [451] = UmecMessageKind.Settings,
        [501] = UmecMessageKind.Settings,
        [504] = UmecMessageKind.Settings,
        [701] = UmecMessageKind.Settings,
        [851] = UmecMessageKind.Settings,
    };

    // Mindray "no valid measurement" sentinels.
    private static readonly HashSet<string> Invalid = new() { "-100", "-1000", "-10000" };

    private static bool IsValidValue(string v) => !Invalid.Contains(v);

    private static string NameOf(int pid, string label)
    {
        if (Parameters.TryGetValue(pid, out var d)) return d.Name;
        return label.Trim().Length > 0 ? label : $"param {pid}";
    }

    /// <summary>
    /// Alarm level as used in OBX 2009 and OBX-4 of alarm messages.
    /// 1 = high matches the "***" prefix the monitor puts on such alarms.
    /// </summary>
    private static AlarmLevel ToAlarmLevel(int code) => code switch
    {
        1 => AlarmLevel.High,
        2 => AlarmLevel.Medium,
        3 => AlarmLevel.Low,
        _ => AlarmLevel.Unknown,
    };

    // ------------------------------------------------------------------
    // HL7 plumbing
    // ------------------------------------------------------------------

    /// <summary>
    /// One segment split into fields, addressed by HL7 number (PID-3 is
    /// <c>seg[3]</c>). MSH is special-cased so that MSH-9 is the message type
    /// as in the standard, even though the '|' after "MSH" is MSH-1. Fields
    /// the monitor left out read as "".
    /// </summary>
    private sealed class Segment
    {
        private readonly string[] _fields;

        public string Name { get; }

        public Segment(string line)
        {
            _fields = line.Split('|');
            Name = _fields[0];
        }

        public string this[int n]
        {
            get
            {
                if (n <= 0) return Name;
                if (Name == "MSH")
                {
                    if (n == 1) return "|";
                    n--;
                }
                return n < _fields.Length ? _fields[n] : "";
            }
        }
    }

    /// <summary>Component <paramref name="i"/> (0-based) of a '^'-separated field; "" when absent.</summary>
    private static string Component(string field, int i)
    {
        int from = 0;
        for (int k = 0; k < i; k++)
        {
            from = field.IndexOf('^', from);
            if (from < 0) return "";
            from++;
        }
        int to = field.IndexOf('^', from);
        return to < 0 ? field[from..] : field[from..to];
    }

    /// <summary>ValueText when the value has a text component, else the value itself.</summary>
    private static string TextOf(UmecObservation o) => o.ValueText.Length > 0 ? o.ValueText : o.Value;

    /// <summary>
    /// Cyrillic text arrives byte-shifted by +0x10 relative to Windows-1251
    /// ('ЗББ' -> 'ЧСС' = HR). Un-shift and decode; pure ASCII passes through.
    /// </summary>
    private static string DecodeText(string raw)
    {
        if (raw.All(c => c < 0x80)) return raw;
        var bytes = Latin1.GetBytes(raw);
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] >= 0x80 && bytes[i] <= 0xEF) bytes[i] += 0x10;
        }
        return Cp1251.GetString(bytes);
    }

    private static double? ParseNumber(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static int? ParseInt(string s) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>"1" -> true, "0" -> false, anything else -> null.</summary>
    private static bool? ParseFlag(string s) => s switch { "1" => true, "0" => false, _ => null };

    /// <summary>"20260707191824" -> monitor local time; all zeros or garbage -> null.</summary>
    private static DateTime? ParseTimestamp(string s) =>
        DateTime.TryParseExact(s, "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var dt) ? dt : null;

    /// <summary>"20080420" -> date; "00000000" or garbage -> null.</summary>
    private static DateOnly? ParseDate(string s) =>
        DateOnly.TryParseExact(s, "yyyyMMdd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var d) ? d : null;

    /// <summary>"3232235620" -> "192.168.0.100" (network byte order); anything else is returned as-is.</summary>
    private static string IpFromUint(string s) =>
        uint.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? $"{n >> 24}.{(n >> 16) & 255}.{(n >> 8) & 255}.{n & 255}"
            : s;
}
