namespace MedicalMonitorServer.Contracts.Dtos;

public sealed class AlarmSettingDto
{
    public int ParameterId { get; init; }

    public string ParameterName { get; init; } = string.Empty;

    public string Unit { get; init; } = string.Empty;

    public double? HighLimit { get; init; }

    public double? LowLimit { get; init; }

    public bool? Enabled { get; init; }

    public AlarmLevelDto? Level { get; init; }

    public int? LevelCode { get; init; }
}

public sealed class ActiveAlarmDto
{
    public AlarmCategoryDto Category { get; init; }

    public int TypeCode { get; init; }

    public int AlarmId { get; init; }

    public string Text { get; init; } = string.Empty;

    public AlarmLevelDto Level { get; init; }

    public int? LevelCode { get; init; }

    public DateTime? RaisedAt { get; init; }
}

public sealed class AlarmSystemStatusDto
{
    public int? Volume { get; init; }

    public bool? Muted { get; init; }

    public bool? AlarmsOff { get; init; }

    public bool? SoundPaused { get; init; }

    public bool? AlarmPaused { get; init; }
}
