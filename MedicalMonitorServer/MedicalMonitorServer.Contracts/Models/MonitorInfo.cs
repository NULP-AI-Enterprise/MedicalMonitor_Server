namespace MedicalMonitorServer.Contracts.Models;

/// <summary>Monitor identity and state from the ADT^A01 beacon (OBX 2304-2307, 4524-4530).</summary>
public sealed class MonitorInfo
{
    /// <summary>OBX 2304.</summary>
    public string Name { get; set; } = "";

    /// <summary>OBX 2305.</summary>
    public bool? Standby { get; set; }

    /// <summary>OBX 2307: 0 none, 1 physiological, 2 technical.</summary>
    public int? HighestAlarmType { get; set; }

    /// <summary>OBX 4530.</summary>
    public bool? HighestAlarmConfirmed { get; set; }

    /// <summary>OBX 4524: how many more HL7 (ORU) connections the monitor accepts.</summary>
    public int? FreeConnections { get; set; }

    /// <summary>OBX 2211.</summary>
    public int? IpSequence { get; set; }

    /// <summary>OBX 4526.</summary>
    public string TelemetrySerial { get; set; } = "";

    /// <summary>OBX 4527.</summary>
    public string DeviceId { get; set; } = "";

    /// <summary>OBX 4528.</summary>
    public string MachineType { get; set; } = "";

    /// <summary>OBX 4529.</summary>
    public string MachineVersion { get; set; } = "";

    /// <summary>OBX 2319.</summary>
    public string ViewBedDeviceId { get; set; } = "";

    /// <summary>OBX 2320.</summary>
    public int? ViewBedIdLength { get; set; }
}
