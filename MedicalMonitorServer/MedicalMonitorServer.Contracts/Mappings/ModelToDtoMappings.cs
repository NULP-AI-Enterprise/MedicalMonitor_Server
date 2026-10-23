using MedicalMonitorServer.Contracts.Dtos;
using MedicalMonitorServer.Contracts.Models;

namespace MedicalMonitorServer.Contracts.Mappings;

public static class ModelToDtoMappings
{
    public static DeviceStateDto ToDto(this DeviceState model) => new()
    {
        Id = model.Id,
        Name = model.Name,
        IpAddress = model.IpAddress,
        Port = model.Port,
        State = model.State.ToDto(),
    };

    public static PatientDataDto ToDto(this PatientData model) => new()
    {
        PatientId = model.PatientId,
        Name = model.Name,
        Values = model.Values.ToDictionary(entry => entry.Key, entry => entry.Value),
    };

    public static PatientInfoDto ToDto(this PatientInfo model) => new()
    {
        PatientId = model.PatientId,
        LastName = model.LastName,
        FirstName = model.FirstName,
        FullName = model.FullName,
        DateOfBirth = model.DateOfBirth,
        Sex = model.Sex,
        PatientClass = model.PatientClass,
        PatientType = model.PatientType.ToDto(),
        Department = model.Department,
        BedNumber = model.BedNumber,
        BedLabel = model.BedLabel,
        MonitorIp = model.MonitorIp,
        MonitorPort = model.MonitorPort,
        Admitted = model.Admitted,
        AdmitDate = model.AdmitDate,
        HeightCm = model.HeightCm,
        WeightKg = model.WeightKg,
        BloodType = model.BloodType,
        Paced = model.Paced,
    };

    public static MonitorInfoDto ToDto(this MonitorInfo model) => new()
    {
        Name = model.Name,
        Standby = model.Standby,
        HighestAlarmType = model.HighestAlarmType,
        HighestAlarmConfirmed = model.HighestAlarmConfirmed,
        FreeConnections = model.FreeConnections,
        IpSequence = model.IpSequence,
        TelemetrySerial = model.TelemetrySerial,
        DeviceId = model.DeviceId,
        MachineType = model.MachineType,
        MachineVersion = model.MachineVersion,
        ViewBedDeviceId = model.ViewBedDeviceId,
        ViewBedIdLength = model.ViewBedIdLength,
    };

    public static VitalSignDto ToDto(this VitalSign model) => new()
    {
        ParameterId = model.ParameterId,
        Name = model.Name,
        Label = model.Label,
        Unit = model.Unit,
        Value = model.Value,
        RawValue = model.RawValue,
        ModuleId = model.ModuleId,
        IsAperiodic = model.IsAperiodic,
        MeasuredAt = model.MeasuredAt,
    };

    public static ParameterLabelDto ToDto(this ParameterLabel model) => new()
    {
        ParameterId = model.ParameterId,
        Label = model.Label,
        GroupId = model.GroupId,
    };

    public static ParameterGroupDto ToDto(this ParameterGroup model) => new()
    {
        GroupId = model.GroupId,
        Label = model.Label,
    };

    public static AlarmSettingDto ToDto(this AlarmSetting model) => new()
    {
        ParameterId = model.ParameterId,
        ParameterName = model.ParameterName,
        Unit = model.Unit,
        HighLimit = model.HighLimit,
        LowLimit = model.LowLimit,
        Enabled = model.Enabled,
        Level = model.Level?.ToDto(),
        LevelCode = model.LevelCode,
    };

    public static ActiveAlarmDto ToDto(this ActiveAlarm model) => new()
    {
        Category = model.Category.ToDto(),
        TypeCode = model.TypeCode,
        AlarmId = model.AlarmId,
        Text = model.Text,
        Level = model.Level.ToDto(),
        LevelCode = model.LevelCode,
        RaisedAt = model.RaisedAt,
    };

    public static AlarmSystemStatusDto ToDto(this AlarmSystemStatus model) => new()
    {
        Volume = model.Volume,
        Muted = model.Muted,
        AlarmsOff = model.AlarmsOff,
        SoundPaused = model.SoundPaused,
        AlarmPaused = model.AlarmPaused,
    };

    public static UmecMessageSnapshotDto ToSnapshotDto(this UmecMessage model) => new()
    {
        Kind = model.Kind.ToDto(),
        ControlId = model.ControlId,
        MessageId = model.MessageId,
        MessageType = model.MessageType,
        Hl7Version = model.Hl7Version,
        ReceivedAt = model.ReceivedAt,
        Oru = model.Oru?.ToSnapshotDto(),
        Adt = model.Adt?.ToDto(),
    };

    public static UmecVitalsSnapshotDto ToSnapshotDto(this UmecVitalsMessage model) => new()
    {
        Patient = model.Patient?.ToDto(),
        Vitals = model.Vitals.Select(ToDto).ToArray(),
        AlarmSettings = model.AlarmSettings.Select(ToDto).ToArray(),
        Alarms = model.Alarms.Select(ToDto).ToArray(),
        ParameterLabels = model.ParameterLabels.Select(ToDto).ToArray(),
        ParameterGroups = model.ParameterGroups.Select(ToDto).ToArray(),
        AlarmSystem = model.AlarmSystem?.ToDto(),
    };

    public static DeviceAdtInfoDto ToDto(this DeviceAdtInfo model) => new()
    {
        Monitor = model.Monitor?.ToDto(),
        Patient = model.Patient?.ToDto(),
    };

    public static DeviceStateEnumDto ToDto(this DeviceStateEnum model) => model switch
    {
        DeviceStateEnum.Disconnected => DeviceStateEnumDto.Disconnected,
        DeviceStateEnum.Connected => DeviceStateEnumDto.Connected,
        DeviceStateEnum.Error => DeviceStateEnumDto.Error,
        _ => DeviceStateEnumDto.Disconnected,
    };

    public static PatientTypeDto ToDto(this PatientType model) => model switch
    {
        PatientType.Unknown => PatientTypeDto.Unknown,
        PatientType.Adult => PatientTypeDto.Adult,
        PatientType.Pediatric => PatientTypeDto.Pediatric,
        PatientType.Neonate => PatientTypeDto.Neonate,
        _ => PatientTypeDto.Unknown,
    };

    public static AlarmLevelDto ToDto(this AlarmLevel model) => model switch
    {
        AlarmLevel.Unknown => AlarmLevelDto.Unknown,
        AlarmLevel.High => AlarmLevelDto.High,
        AlarmLevel.Medium => AlarmLevelDto.Medium,
        AlarmLevel.Low => AlarmLevelDto.Low,
        _ => AlarmLevelDto.Unknown,
    };

    public static AlarmCategoryDto ToDto(this AlarmCategory model) => model switch
    {
        AlarmCategory.Unknown => AlarmCategoryDto.Unknown,
        AlarmCategory.Physiological => AlarmCategoryDto.Physiological,
        AlarmCategory.Technical => AlarmCategoryDto.Technical,
        _ => AlarmCategoryDto.Unknown,
    };

    public static UmecMessageKindDto ToDto(this UmecMessageKind model) => model switch
    {
        UmecMessageKind.Unknown => UmecMessageKindDto.Unknown,
        UmecMessageKind.PatientInfo => UmecMessageKindDto.PatientInfo,
        UmecMessageKind.ParameterLabels => UmecMessageKindDto.ParameterLabels,
        UmecMessageKind.AlarmLimits => UmecMessageKindDto.AlarmLimits,
        UmecMessageKind.AlarmSwitches => UmecMessageKindDto.AlarmSwitches,
        UmecMessageKind.AlarmLevels => UmecMessageKindDto.AlarmLevels,
        UmecMessageKind.AlarmSystemStatus => UmecMessageKindDto.AlarmSystemStatus,
        UmecMessageKind.PhysiologicalAlarms => UmecMessageKindDto.PhysiologicalAlarms,
        UmecMessageKind.TechnicalAlarms => UmecMessageKindDto.TechnicalAlarms,
        UmecMessageKind.Vitals => UmecMessageKindDto.Vitals,
        UmecMessageKind.Settings => UmecMessageKindDto.Settings,
        UmecMessageKind.MonitorStatus => UmecMessageKindDto.MonitorStatus,
        UmecMessageKind.AdtEvent => UmecMessageKindDto.AdtEvent,
        _ => UmecMessageKindDto.Unknown,
    };
}
