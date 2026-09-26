namespace MedicalMonitorServer.Contracts.Models;

/// <summary>Display label the monitor uses for a parameter (OBX 2025), e.g. 101 -> "ЧСС".</summary>
public sealed class ParameterLabel
{
    public int ParameterId { get; set; }

    public string Label { get; set; } = "";

    /// <summary>Module the parameter belongs to (OBX-4), e.g. 2101 = ECG.</summary>
    public int? GroupId { get; set; }
}

/// <summary>Display label of a parameter group / module (OBX 2023), e.g. 2101 -> "ЭКГ".</summary>
public sealed class ParameterGroup
{
    public int GroupId { get; set; }

    public string Label { get; set; } = "";
}
