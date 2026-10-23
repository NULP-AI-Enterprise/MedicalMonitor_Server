namespace PatientMonitoring.Api.Models;

public class VitalSign
{
    public long Id { get; set; }
    public Guid PatientId { get; set; }
    public Patient? Patient { get; set; }

    /// <summary>Момент вимірювання на пристрої (UTC).</summary>
    public DateTime RecordedAt { get; set; }

    /// <summary>Момент отримання сервером (UTC).</summary>
    public DateTime ReceivedAt { get; set; }

    public string? DeviceId { get; set; }

    /// <summary>Пульс, уд/хв.</summary>
    public int? HeartRate { get; set; }

    /// <summary>Систолічний тиск, мм рт. ст.</summary>
    public int? SystolicBloodPressure { get; set; }

    /// <summary>Діастолічний тиск, мм рт. ст.</summary>
    public int? DiastolicBloodPressure { get; set; }

    /// <summary>Сатурація SpO₂, %.</summary>
    public double? OxygenSaturation { get; set; }

    /// <summary>Температура тіла, °C.</summary>
    public double? Temperature { get; set; }

    /// <summary>Частота дихання, за хвилину.</summary>
    public int? RespiratoryRate { get; set; }

    public VitalStatus Status { get; set; }
}
