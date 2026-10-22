namespace MedicalMonitorServer.Contracts.Dtos;

public enum DeviceStateEnumDto
{
    Disconnected = 0,
    Connected = 1,
    Error = 2,
}

public enum PatientTypeDto
{
    Unknown = 0,
    Adult,
    Pediatric,
    Neonate,
}

public enum AlarmLevelDto
{
    Unknown = 0,
    High,
    Medium,
    Low,
}

public enum AlarmCategoryDto
{
    Unknown = 0,
    Physiological,
    Technical,
}

public enum UmecMessageKindDto
{
    Unknown = 0,
    PatientInfo,
    ParameterLabels,
    AlarmLimits,
    AlarmSwitches,
    AlarmLevels,
    AlarmSystemStatus,
    PhysiologicalAlarms,
    TechnicalAlarms,
    Vitals,
    Settings,
    MonitorStatus,
    AdtEvent,
}
