namespace MedicalMonitorServer.Contracts.Models;

public enum AlarmCategory
{
    Unknown = 0,

    /// <summary>Patient condition, e.g. "HR too low" (OBX-3 = 1).</summary>
    Physiological,

    /// <summary>Equipment condition, e.g. "ECG lead off", "NIBP cuff disconnected" (OBX-3 = 3, 4).</summary>
    Technical,
}

/// <summary>One alarm that is active on the monitor right now.</summary>
public sealed class ActiveAlarm
{
    public AlarmCategory Category { get; set; }

    /// <summary>OBX-3 as sent: 1 physiological, 3 / 4 technical.</summary>
    public int TypeCode { get; set; }

    /// <summary>Mindray alarm id (first component of OBX-5), e.g. 10002 = HR too low.</summary>
    public int AlarmId { get; set; }

    /// <summary>Alarm text as shown on the monitor, priority asterisks removed.</summary>
    public string Text { get; set; } = "";

    /// <summary>
    /// From the "***" / "**" / "*" prefix of the text when present, otherwise from OBX-4.
    /// </summary>
    public AlarmLevel Level { get; set; }

    /// <summary>OBX-4 as sent by the monitor, when present.</summary>
    public int? LevelCode { get; set; }

    /// <summary>OBX-14, monitor local time.</summary>
    public DateTime? RaisedAt { get; set; }
}
