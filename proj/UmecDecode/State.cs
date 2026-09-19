using static UmecDecode.Hl7;

namespace UmecDecode;

sealed class Patient
{
    public string Name = "", Id = "", Dob = "", Sex = "", Bed = "", Height = "", Weight = "";
}

/// <summary>Everything decoded so far from the ORU stream.</summary>
sealed class State
{
    public readonly Dictionary<string, string> LabelMap = new();   // pid -> label from OBX 2025
    public readonly Dictionary<string, string> Measured = new();   // pid -> last value
    public readonly Dictionary<string, string> Hi = new(), Lo = new(), Lvl = new(), OnOff = new();
    public readonly Patient Patient = new();
    public int MsgCount;
    public DateTime? LastChange;

    public string Name(string pid)
    {
        if (Param.TryGetValue(pid, out var p)) return p.Name;
        if (LabelMap.TryGetValue(pid, out var label) && label.Trim().Length > 0) return label;
        return $"param {pid}";
    }

    public string Unit(string pid) => Param.TryGetValue(pid, out var p) ? p.Unit : "";

    /// <summary>Measured parameter ids in numeric order, without weight/height (51/52).</summary>
    public IEnumerable<string> MeasuredIds() =>
        Measured.Keys.Where(k => k is not ("51" or "52")).OrderBy(SortKey);

    /// <summary>Fold one HL7 message (segments separated by CR) into the state.</summary>
    public void Apply(string msg)
    {
        MsgCount++;
        foreach (var seg in msg.Split('\r'))
        {
            var f = seg.Split('|');
            switch (f[0])
            {
                case "PID" when f.Length > 5:
                {
                    var nm = f[5].Split('^').Where(x => x.Length > 0);
                    Patient.Name = Deshift(string.Join(" ", nm)).Trim();
                    Patient.Id = f[3];
                    if (f.Length > 7) Patient.Dob = f[7];
                    if (f.Length > 8) Patient.Sex = f[8];
                    break;
                }
                case "PV1" when f.Length > 3:
                {
                    var bed = f[3].Split('^').FirstOrDefault(p => IsDigits(p) && p.Length <= 4);
                    if (bed != null) Patient.Bed = bed;
                    break;
                }
                case "OBX" when f.Length > 5:
                    ApplyObx(f);
                    break;
            }
        }
    }

    void ApplyObx(string[] f)
    {
        var code3 = f[3].Split('^');            // "<code>^<label>"
        string code = code3[0];
        string label = code3.Length > 1 ? code3[1] : "";
        string sub = f[4];                      // parameter id the OBX refers to
        string vs = f[5];

        switch (code)
        {
            case "2025":                        // label announcement: "<pid>^<name>"
            {
                var v = vs.Split('^');
                LabelMap[v[0]] = Deshift(v.Length > 1 ? v[1] : "");
                break;
            }
            case "2002": Hi[sub] = vs; break;
            case "2003": Lo[sub] = vs; break;
            case "2009": Lvl[sub] = vs.Split('^')[0]; break;
            case "2004": OnOff[sub] = vs.Split('^')[0]; break;
            case "2301": Patient.Bed = vs; break;
            case "51": Patient.Weight = vs; break;
            case "52": Patient.Height = vs; break;
            default:
                if (label.Length == 0) break;
                if (Param.ContainsKey(code))
                {
                    if (!Measured.TryGetValue(code, out var old) || old != vs)
                    {
                        Measured[code] = vs;
                        LastChange = DateTime.Now;
                    }
                }
                else if (IsDigits(code) && int.TryParse(code, out var n) && n < 1000)
                {
                    Measured.TryAdd(code, vs);
                }
                break;
        }
    }
}
