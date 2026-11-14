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

/// <summary>Display label the monitor uses for a parameter (OBX 2025), e.g. 101 -> "ЧСС".</summary>
public sealed class ParameterLabel
{
    public int ParameterId { get; set; }

    public string Label { get; set; } = "";

    /// <summary>Module the parameter belongs to (OBX-4), e.g. 2101 = ECG.</summary>
    public int? GroupId { get; set; }
}

/// <summary>Display label of a parameter group / module (OBX 2023), e.g. 2101 -> "ЕКГ".</summary>
public sealed class ParameterGroup
{
    public int GroupId { get; set; }

    public string Label { get; set; } = "";
}

/// <summary>
/// Description of one waveform channel (OBX 2153 "CHN", value type CD), e.g.
/// <c>1101^II^0.019531&amp;mv^1.000000&amp;128^256^0&amp;255</c> = ECG lead II.
/// Only the descriptor travels over HL7; the samples themselves are not in
/// the TCP 4601 stream.
/// </summary>
public sealed class WaveChannel
{
    /// <summary>Channel id, e.g. 1101 = ECG 1, 1151 = respiration, 1152 = Pleth.</summary>
    public int ChannelId { get; set; }

    /// <summary>Label shown on the monitor, decoded: "II", "Дых.", "Плет.".</summary>
    public string Label { get; set; } = "";

    /// <summary>Physical value per sample step (0.019531 for ECG); null when the monitor gives none.</summary>
    public double? Scale { get; set; }

    /// <summary>Unit of <see cref="Scale"/>: "mv" for ECG, empty for respiration / Pleth.</summary>
    public string ScaleUnit { get; set; } = "";

    /// <summary>First number of the fourth component (1.0 in every capture).</summary>
    public double? Gain { get; set; }

    /// <summary>Sample value that is zero on the trace (128 for ECG / respiration, 0 for Pleth).</summary>
    public int? Baseline { get; set; }

    /// <summary>Number of distinct sample levels (256 = 8 bit).</summary>
    public int? Levels { get; set; }

    public int? MinSample { get; set; }

    public int? MaxSample { get; set; }
}
