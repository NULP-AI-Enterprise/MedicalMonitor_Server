namespace MedicalMonitorServer.Contracts.Dtos;

public sealed class VitalSignDto
{
    public int ParameterId { get; init; }

    public string Name { get; init; } = string.Empty;

    public string Label { get; init; } = string.Empty;

    public string Unit { get; init; } = string.Empty;

    public double? Value { get; init; }

    public string RawValue { get; init; } = string.Empty;

    public int? ModuleId { get; init; }

    public bool IsAperiodic { get; init; }

    public DateTime? MeasuredAt { get; init; }
}

public sealed class ParameterLabelDto
{
    public int ParameterId { get; init; }

    public string Label { get; init; } = string.Empty;

    public int? GroupId { get; init; }
}

public sealed class ParameterGroupDto
{
    public int GroupId { get; init; }

    public string Label { get; init; } = string.Empty;
}
