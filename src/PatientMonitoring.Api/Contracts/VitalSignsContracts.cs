using System.ComponentModel.DataAnnotations;
using PatientMonitoring.Api.Models;

namespace PatientMonitoring.Api.Contracts;

/// <summary>
/// Показники, які надсилає пристрій/сенсор. Усі поля необов'язкові, але хоча б одне має бути заповнене.
/// </summary>
public sealed record RecordVitalSignsRequest : IValidatableObject
{
    /// <summary>Час вимірювання. Якщо не вказано — використовується час отримання сервером.</summary>
    public DateTimeOffset? RecordedAt { get; init; }

    [MaxLength(100)]
    public string? DeviceId { get; init; }

    [Range(0, 300)]
    public int? HeartRate { get; init; }

    [Range(0, 300)]
    public int? SystolicBloodPressure { get; init; }

    [Range(0, 200)]
    public int? DiastolicBloodPressure { get; init; }

    [Range(0, 100)]
    public double? OxygenSaturation { get; init; }

    [Range(20, 45)]
    public double? Temperature { get; init; }

    [Range(0, 100)]
    public int? RespiratoryRate { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var hasAnyValue = HeartRate is not null
            || SystolicBloodPressure is not null
            || DiastolicBloodPressure is not null
            || OxygenSaturation is not null
            || Temperature is not null
            || RespiratoryRate is not null;

        if (!hasAnyValue)
        {
            yield return new ValidationResult("At least one vital sign value must be provided.");
        }

        if (SystolicBloodPressure is { } systolic && DiastolicBloodPressure is { } diastolic && diastolic >= systolic)
        {
            yield return new ValidationResult(
                "Diastolic blood pressure must be lower than systolic.",
                [nameof(DiastolicBloodPressure)]);
        }

        if (RecordedAt is { } recordedAt && recordedAt > DateTimeOffset.UtcNow.AddMinutes(5))
        {
            yield return new ValidationResult("RecordedAt cannot be in the future.", [nameof(RecordedAt)]);
        }
    }
}

/// <summary>Показник, що вийшов за межі норми.</summary>
public sealed record VitalAlert(
    string Parameter,
    double Value,
    VitalStatus Status,
    string Message);

public sealed record VitalSignsResponse(
    long Id,
    Guid PatientId,
    DateTime RecordedAt,
    DateTime ReceivedAt,
    string? DeviceId,
    int? HeartRate,
    int? SystolicBloodPressure,
    int? DiastolicBloodPressure,
    double? OxygenSaturation,
    double? Temperature,
    int? RespiratoryRate,
    VitalStatus Status,
    IReadOnlyList<VitalAlert> Alerts,
    string? PatientFullName = null);

/// <summary>Сповіщення, яке отримують SignalR-клієнти, коли стан пацієнта виходить за межі норми.</summary>
public sealed record PatientAlertNotification(
    Guid PatientId,
    string PatientFullName,
    VitalStatus Status,
    DateTime RecordedAt,
    IReadOnlyList<VitalAlert> Alerts);

/// <summary>Пакет показників для завантаження в автономному режимі.</summary>
public sealed record RecordVitalSignsBatchRequest
{
    [Required, MinLength(1), MaxLength(500)]
    public required IReadOnlyList<RecordVitalSignsRequest> Items { get; init; }
}

public sealed record RecordVitalSignsBatchResponse(
    Guid PatientId,
    int InsertedCount,
    VitalSignsResponse? LatestRecorded);

/// <summary>
/// Універсальна модель для парсерів медичного обладнання.
/// Дозволяє ідентифікувати пацієнта не лише за ID, а й за палатою/ліжком або ПІП.
/// </summary>
public sealed record IngestVitalSignsRequest : IValidatableObject
{
    public Guid? PatientId { get; init; }

    [MaxLength(50)]
    public string? Ward { get; init; }

    [MaxLength(20)]
    public string? Bed { get; init; }

    [MaxLength(200)]
    public string? PatientFullName { get; init; }

    [MaxLength(100)]
    public string? DeviceId { get; init; }

    public DateTimeOffset? RecordedAt { get; init; }

    [Range(0, 300)]
    public int? HeartRate { get; init; }

    [Range(0, 300)]
    public int? SystolicBloodPressure { get; init; }

    [Range(0, 200)]
    public int? DiastolicBloodPressure { get; init; }

    [Range(0, 100)]
    public double? OxygenSaturation { get; init; }

    [Range(20, 45)]
    public double? Temperature { get; init; }

    [Range(0, 100)]
    public int? RespiratoryRate { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (PatientId is null && string.IsNullOrWhiteSpace(Ward) && string.IsNullOrWhiteSpace(PatientFullName))
        {
            yield return new ValidationResult(
                "Patient must be identified by either PatientId, Ward (+ optional Bed), or PatientFullName.",
                [nameof(PatientId), nameof(Ward), nameof(PatientFullName)]);
        }

        var hasAnyValue = HeartRate is not null
            || SystolicBloodPressure is not null
            || DiastolicBloodPressure is not null
            || OxygenSaturation is not null
            || Temperature is not null
            || RespiratoryRate is not null;

        if (!hasAnyValue)
        {
            yield return new ValidationResult("At least one vital sign value must be provided.");
        }

        if (SystolicBloodPressure is { } systolic && DiastolicBloodPressure is { } diastolic && diastolic >= systolic)
        {
            yield return new ValidationResult(
                "Diastolic blood pressure must be lower than systolic.",
                [nameof(DiastolicBloodPressure)]);
        }
    }
}

