namespace MedicalMonitorServer.Contracts.Dtos;

public sealed class UmecMessageSnapshotDto
{
    public UmecMessageKindDto Kind { get; init; }

    public string ControlId { get; init; } = string.Empty;

    public int MessageId { get; init; }

    public string MessageType { get; init; } = string.Empty;

    public string Hl7Version { get; init; } = string.Empty;

    public DateTime ReceivedAt { get; init; }

    public UmecVitalsSnapshotDto? Oru { get; init; }

    public DeviceAdtInfoDto? Adt { get; init; }
}

/// <summary>
/// API-facing view of decoded ORU data. Raw OBX observations are intentionally omitted.
/// </summary>
public sealed class UmecVitalsSnapshotDto
{
    public PatientInfoDto? Patient { get; init; }

    public IReadOnlyList<VitalSignDto> Vitals { get; init; } = Array.Empty<VitalSignDto>();

    public IReadOnlyList<AlarmSettingDto> AlarmSettings { get; init; } = Array.Empty<AlarmSettingDto>();

    public IReadOnlyList<ActiveAlarmDto> Alarms { get; init; } = Array.Empty<ActiveAlarmDto>();

    public IReadOnlyList<ParameterLabelDto> ParameterLabels { get; init; } = Array.Empty<ParameterLabelDto>();

    public IReadOnlyList<ParameterGroupDto> ParameterGroups { get; init; } = Array.Empty<ParameterGroupDto>();

    public AlarmSystemStatusDto? AlarmSystem { get; init; }
}

/// <summary>
/// API-facing view of decoded ADT monitor/patient data. Raw OBX observations are intentionally omitted.
/// </summary>
public sealed class DeviceAdtInfoDto
{
    public MonitorInfoDto? Monitor { get; init; }

    public PatientInfoDto? Patient { get; init; }
}
