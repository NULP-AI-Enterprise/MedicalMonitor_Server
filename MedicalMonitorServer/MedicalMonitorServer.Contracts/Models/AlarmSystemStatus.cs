namespace MedicalMonitorServer.Contracts.Models;

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
