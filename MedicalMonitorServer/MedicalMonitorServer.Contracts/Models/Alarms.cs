namespace MedicalMonitorServer.Contracts.Models;

public enum AlarmLevel
{
    Unknown = 0,
    High,
    Medium,
    Low,
}

public enum AlarmCategory
{
    Unknown = 0,

    /// <summary>Patient condition, e.g. "HR too low" (OBX-3 = 1).</summary>
    Physiological,

    /// <summary>Equipment condition, e.g. "ECG lead off", "NIBP cuff disconnected" (OBX-3 = 3, 4).</summary>
    Technical,
}

/// <summary>
/// Alarm configuration for one parameter. The monitor sends limits (msg 51),
/// on/off (msg 60) and level (msg 58) in separate messages, so only the
/// fields carried by the current message are non-null.
/// </summary>
public sealed class AlarmSetting
{
    public int ParameterId { get; set; }

    public string ParameterName { get; set; } = "";

    public string Unit { get; set; } = "";

    /// <summary>OBX 2002.</summary>
    public double? HighLimit { get; set; }

    /// <summary>OBX 2003.</summary>
    public double? LowLimit { get; set; }

    /// <summary>OBX 2004.</summary>
    public bool? Enabled { get; set; }

    /// <summary>OBX 2009, decoded.</summary>
    public AlarmLevel? Level { get; set; }

    /// <summary>OBX 2009 as sent by the monitor.</summary>
    public int? LevelCode { get; set; }
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

/// <summary>Monitor-wide alarm state (msg 53).</summary>
public sealed class AlarmSystemStatus
{
    /// <summary>OBX 2013, alarm volume.</summary>
    public int? Volume { get; set; }

    /// <summary>OBX 2014 "Alarm Mute".</summary>
    public bool? Muted { get; set; }

    /// <summary>OBX 2027 "Alm off".</summary>
    public bool? AlarmsOff { get; set; }

    /// <summary>OBX 2028 "Alm Sound Pause".</summary>
    public bool? SoundPaused { get; set; }

    /// <summary>OBX 2016 "Alarm Pause".</summary>
    public bool? AlarmPaused { get; set; }
}
