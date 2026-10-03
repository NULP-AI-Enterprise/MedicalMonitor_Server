namespace MedicalMonitorServer.Contracts.Models;

public abstract class UmecHl7Message
{
    public string? MessageType { get; init; }
    public string? ControlId { get; init; }
    public string? Hl7Version { get; init; }
    public DateTimeOffset ReceivedAt { get; init; }
    public string Raw { get; init; } = string.Empty;
}

public class UmecAdtMessage : UmecHl7Message
{
    public MonitorInfo? Monitor { get; init; }
    public PatientInfo? Patient { get; init; }
    public string? EventCode { get; init; }
    public DateTimeOffset? EventTimestamp { get; init; }
}

// Compatibility alias for legacy ATD naming in some integrations.
public sealed class UmecAtdMessage : UmecAdtMessage
{
}

public sealed class UmecOruMessage : UmecHl7Message
{
    public PatientInfo? Patient { get; init; }
    public List<VitalSign> VitalSigns { get; init; } = [];
    public List<AlarmSetting> AlarmSettings { get; init; } = [];
    public AlarmSystemStatus? AlarmSystemStatus { get; init; }
    public List<ActiveAlarm> ActiveAlarms { get; init; } = [];
    public List<ParameterLabel> ParameterLabels { get; init; } = [];
    public List<ParameterGroup> ParameterGroups { get; init; } = [];
    public List<UmecObservation> Observations { get; init; } = [];
}

public sealed class PatientInfo
{
    public string? Id { get; init; }
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public DateTimeOffset? DateOfBirth { get; init; }
    public string? Sex { get; init; }
}

public sealed class MonitorInfo
{
    public string? DeviceId { get; init; }
    public string? Model { get; init; }
    public string? Status { get; init; }
    public string? Location { get; init; }
}

public sealed class VitalSign
{
    public string? Code { get; init; }
    public string? Name { get; init; }
    public double Value { get; init; }
    public string? Units { get; init; }
}

public sealed class ActiveAlarm
{
    public string? Code { get; init; }
    public string? Severity { get; init; }
    public string? Description { get; init; }
}

public sealed class AlarmSetting
{
    public string? Code { get; init; }
    public string? Value { get; init; }
}

public sealed class AlarmSystemStatus
{
    public string? Status { get; init; }
    public string? Details { get; init; }
}

public sealed class ParameterLabel
{
    public string? Code { get; init; }
    public string? Label { get; init; }
    public string? Group { get; init; }
}

public sealed class ParameterGroup
{
    public string? Name { get; init; }
    public List<string> ParameterCodes { get; init; } = [];
}

public sealed class UmecObservation
{
    public string? Code { get; init; }
    public string? Name { get; init; }
    public string? Group { get; init; }
    public string? Value { get; init; }
    public string? Units { get; init; }
    public string? Status { get; init; }
}
