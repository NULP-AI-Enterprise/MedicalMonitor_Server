namespace UmecAdt;

/// <summary>What we know about one monitor, keyed by the address its beacons come from.</summary>
sealed class MonitorState
{
    public string Ip = "";
    public int Port;                    // UDP port the beacon arrived on (4600 / 4620)
    public AdtMessage Last = null!;
    public string LastRaw = "";
    public DateTime FirstSeen, LastSeen;
    public int Count;
    public bool Offline;
}

/// <summary>An ADT event derived from the beacon stream.</summary>
sealed record AdtEvent(DateTime Time, string Kind, MonitorState Monitor, string Detail)
{
    public string ToLine()
    {
        var m = Monitor.Last;
        var who = m.PatientName.Length > 0 ? m.PatientName : "(no patient)";
        return $"   {Time:HH:mm:ss}  {Kind,-9}  {Monitor.Ip,-15}  {who}  {Detail}";
    }
}

/// <summary>
/// Turns the once-per-second ADT^A01 beacons into discrete events:
/// ONLINE / ADMIT / DISCHARGE / UPDATE / OFFLINE, plus any non-A01 ADT
/// message type verbatim (e.g. ADT^A03).
/// </summary>
sealed class Fleet
{
    public readonly SortedDictionary<string, MonitorState> Monitors = new(StringComparer.Ordinal);
    public int Datagrams;

    /// <summary>
    /// Fold one datagram in. Returns the events it caused (usually none);
    /// <paramref name="rawChanged"/> tells whether the datagram text differs
    /// from the previous one from the same monitor (worth archiving).
    /// </summary>
    public List<AdtEvent> Ingest(string ip, int port, string raw, DateTime now, out bool rawChanged)
    {
        var events = new List<AdtEvent>();
        rawChanged = false;
        var msg = AdtMessage.Parse(raw);
        if (msg == null || !msg.Type.StartsWith("ADT", StringComparison.Ordinal)) return events;

        Datagrams++;
        if (!Monitors.TryGetValue(ip, out var mon))
        {
            mon = new MonitorState { Ip = ip, FirstSeen = now };
            Monitors[ip] = mon;
        }
        var prev = mon.Last;
        bool wasOffline = mon.Offline;
        rawChanged = raw != mon.LastRaw;

        mon.Last = msg;
        mon.LastRaw = raw;
        mon.LastSeen = now;
        mon.Port = port;
        mon.Count++;
        mon.Offline = false;

        if (prev == null)
            events.Add(new AdtEvent(now, "ONLINE", mon, $"{msg.Type} udp/{port}"));
        else if (wasOffline)
            events.Add(new AdtEvent(now, "ONLINE", mon, "beacons resumed"));

        if (msg.Type != "ADT^A01")
            events.Add(new AdtEvent(now, msg.Type, mon, $"control id {msg.ControlId}"));

        if (prev != null)
            events.AddRange(Diff(prev, msg, mon, now));
        return events;
    }

    /// <summary>Monitors silent for longer than <paramref name="after"/> go OFFLINE.</summary>
    public List<AdtEvent> ExpireOffline(DateTime now, TimeSpan after)
    {
        var events = new List<AdtEvent>();
        foreach (var mon in Monitors.Values)
        {
            if (mon.Offline || now - mon.LastSeen < after) continue;
            mon.Offline = true;
            events.Add(new AdtEvent(now, "OFFLINE", mon, $"no beacon since {mon.LastSeen:HH:mm:ss}"));
        }
        return events;
    }

    static IEnumerable<AdtEvent> Diff(AdtMessage prev, AdtMessage cur, MonitorState mon, DateTime now)
    {
        bool patientChanged = cur.PatientId != prev.PatientId && cur.PatientId.Length > 0;
        if ((cur.Admitted == true && prev.Admitted != true) || (patientChanged && cur.Admitted != false))
        {
            yield return new AdtEvent(now, "ADMIT", mon, $"id {cur.PatientId}  bed {cur.BedLabel}  type {cur.PatientTypeText}");
            yield break;
        }
        if (cur.Admitted == false && prev.Admitted == true)
        {
            var who = prev.PatientName.Length > 0 ? prev.PatientName : prev.PatientId;
            yield return new AdtEvent(now, "DISCHARGE", mon, $"was {who}");
            yield break;
        }

        var changes = new List<string>();
        void Cmp(string what, string a, string b) { if (a != b) changes.Add($"{what}: '{a}' -> '{b}'"); }
        Cmp("name", prev.PatientName, cur.PatientName);
        Cmp("id", prev.PatientId, cur.PatientId);
        Cmp("bed", prev.BedLabel, cur.BedLabel);
        Cmp("dept", prev.Dept, cur.Dept);
        Cmp("type", prev.PatientTypeText, cur.PatientTypeText);
        Cmp("admit date", prev.AdmitDate, cur.AdmitDate);
        Cmp("monitor", prev.MonitorName, cur.MonitorName);
        Cmp("standby", prev.StandbyText, cur.StandbyText);
        if (changes.Count > 0)
            yield return new AdtEvent(now, "UPDATE", mon, string.Join("; ", changes));
    }
}
