namespace MedicalMonitorServer.Contracts.Models;

public enum AlarmLevel
{
    Unknown = 0,
    High,
    Medium,
    Low,
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
