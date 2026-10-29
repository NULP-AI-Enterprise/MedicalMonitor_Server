namespace MedicalMonitorServer.Contracts.Models;

/// <summary>One measured value (OBX with a numeric parameter id below 1000).</summary>
public sealed class VitalSign
{
    /// <summary>Mindray parameter id: 101 = HR, 160 = SpO2, 170/171/172 = NIBP sys/dia/mean, ...</summary>
    public int ParameterId { get; set; }

    /// <summary>English name from the parameter table, or the monitor's own label when unknown.</summary>
    public string Name { get; set; } = "";

    /// <summary>Label sent by the monitor in OBX-3 (e.g. "Sys"), decoded.</summary>
    public string Label { get; set; } = "";

    public string Unit { get; set; } = "";

    /// <summary>Null when the monitor sent a "no valid measurement" sentinel (-100, -1000, -10000).</summary>
    public double? Value { get; set; }

    public string RawValue { get; set; } = "";

    /// <summary>OBX-4: id of the module that produced the value (2105 = NIBP).</summary>
    public int? ModuleId { get; set; }

    /// <summary>True for episodic measurements such as NIBP (OBX-13 = "APERIODIC").</summary>
    public bool IsAperiodic { get; set; }

    /// <summary>OBX-14, monitor local time; null when the monitor sent all zeros.</summary>
    public DateTime? MeasuredAt { get; set; }
}
