using System.Text;
using static UmecDecode.Hl7;

namespace UmecDecode;

/// <summary>Text report, CSV snapshot and trend log built from a <see cref="State"/>.</summary>
static class Reports
{
    const int Width = 62;

    static string Row(string name, string lo, string hi, string lvl, string unit) =>
        $"   {name,-26} {lo,8} {hi,8}  {lvl,-5} {unit}";

    public static string Render(State st, string src, bool live = false)
    {
        var L = new List<string>();
        L.Add(new string('=', Width));
        L.Add($" Mindray uMec10 — decoded vitals   ({src})");
        if (live)
        {
            var upd = st.LastChange?.ToString("HH:mm:ss") ?? "--:--:--";
            L.Add($" msgs:{st.MsgCount}   last value change:{upd}   {DateTime.Now:HH:mm:ss}");
        }
        L.Add(new string('=', Width));
        var p = st.Patient;
        var id = p.Id.Length > 24 ? p.Id[..24] : p.Id;
        L.Add($" Patient : {(p.Name.Length > 0 ? p.Name : "(none)")}   ID:{id}");
        L.Add($" Sex:{p.Sex}  DOB:{p.Dob}  Bed:{p.Bed}  H:{p.Height} cm  W:{p.Weight} kg");
        L.Add(new string('-', Width));
        L.Add(" MEASURED VITALS");
        var meas = st.MeasuredIds().ToList();
        if (meas.Count > 0)
        {
            foreach (var pid in meas)
                L.Add($"   {st.Name(pid),-26} {FmtMeas(st.Measured[pid]),8} {st.Unit(pid)}");
        }
        else
        {
            L.Add("   (none yet — take an NIBP reading to populate)");
        }
        L.Add(new string('-', Width));
        L.Add(" ALARM LIMITS");
        L.Add(Row("parameter", "low", "high", "level", "unit"));
        foreach (var pid in st.Hi.Keys.Union(st.Lo.Keys).OrderBy(SortKey))
        {
            var lvl = st.Lvl.GetValueOrDefault(pid, "");
            L.Add(Row(st.Name(pid),
                      st.Lo.GetValueOrDefault(pid, "-"),
                      st.Hi.GetValueOrDefault(pid, "-"),
                      Level.GetValueOrDefault(lvl, lvl),
                      st.Unit(pid)));
        }
        return string.Join("\n", L);
    }

    public static void WriteCsv(State st, string path)
    {
        var sb = new StringBuilder("param_id,parameter,measured,low_limit,high_limit,alarm_level,unit\n");
        var ids = st.Measured.Keys.Union(st.Hi.Keys).Union(st.Lo.Keys).OrderBy(SortKey);
        foreach (var pid in ids)
        {
            if (pid is "51" or "52") continue;
            var mv = st.Measured.GetValueOrDefault(pid, "");
            var lvl = Level.GetValueOrDefault(st.Lvl.GetValueOrDefault(pid, ""), "");
            sb.Append($"{pid},{st.Name(pid)},{(IsValid(mv) ? mv : "")},")
              .Append($"{st.Lo.GetValueOrDefault(pid, "")},{st.Hi.GetValueOrDefault(pid, "")},")
              .Append($"{lvl},{st.Unit(pid)}\n");
        }
        File.WriteAllText(path, sb.ToString());
    }

    /// <summary>Append current measured vitals as one timestamped row (for trends).</summary>
    public static void AppendTrend(State st, string path)
    {
        var ids = st.MeasuredIds().ToList();
        var sb = new StringBuilder();
        if (!File.Exists(path))
            sb.Append("timestamp,").AppendJoin(',', ids.Select(st.Name)).Append('\n');
        sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append(',')
          .AppendJoin(',', ids.Select(i => IsValid(st.Measured[i]) ? st.Measured[i] : ""))
          .Append('\n');
        File.AppendAllText(path, sb.ToString());
    }
}
