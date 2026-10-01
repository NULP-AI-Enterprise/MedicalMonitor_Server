namespace PatientMonitoring.Api.Contracts;

/// <summary>
/// DTO повідомлення показників монітора Mindray uMEC (UmecVitalsMessageDto),
/// отриманого та десереалізованого через IParserService.
/// </summary>
public sealed record UmecVitalsMessageDto
{
    public string? RawMessage { get; init; }
    public string? MessageType { get; init; }
    public string? PatientId { get; init; }
    public string? PatientFullName { get; init; }
    public string? Ward { get; init; }
    public string? Bed { get; init; }
    public string? DeviceId { get; init; }
    public DateTimeOffset? RecordedAt { get; init; }
    public int? HeartRate { get; init; }
    public int? SystolicBloodPressure { get; init; }
    public int? DiastolicBloodPressure { get; init; }
    public double? OxygenSaturation { get; init; }
    public double? Temperature { get; init; }
    public int? RespiratoryRate { get; init; }
    public IReadOnlyList<string> Alarms { get; init; } = [];
}

/// <summary>
/// Запит на прийом та парсинг сирого HL7 / ORU / ADT повідомлення від Mindray uMEC.
/// </summary>
public sealed record IngestUmecHl7Request
{
    public required string RawMessage { get; init; }
    public string? DeviceId { get; init; }
}

public sealed record IngestUmecHl7Response(
    bool Success,
    string MessageKind,
    Guid? PatientId,
    string? PatientFullName,
    VitalSignsResponse? Vitals,
    string? Details);
