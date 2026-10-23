using PatientMonitoring.Api.Models;

namespace PatientMonitoring.Api.Services;

/// <summary>
/// Пороги норми для показників. Значення за замовчуванням можна перевизначити
/// в секції "VitalThresholds" файлу appsettings.json.
/// </summary>
public sealed class VitalThresholds
{
    public const string SectionName = "VitalThresholds";

    public ThresholdRange HeartRate { get; set; } = new()
    {
        CriticalLow = 40, WarningLow = 50, WarningHigh = 110, CriticalHigh = 130
    };

    public ThresholdRange SystolicBloodPressure { get; set; } = new()
    {
        CriticalLow = 80, WarningLow = 90, WarningHigh = 140, CriticalHigh = 180
    };

    public ThresholdRange DiastolicBloodPressure { get; set; } = new()
    {
        CriticalLow = 40, WarningLow = 60, WarningHigh = 90, CriticalHigh = 120
    };

    public ThresholdRange OxygenSaturation { get; set; } = new()
    {
        CriticalLow = 88, WarningLow = 92
    };

    public ThresholdRange Temperature { get; set; } = new()
    {
        CriticalLow = 35.0, WarningLow = 36.0, WarningHigh = 38.0, CriticalHigh = 39.5
    };

    public ThresholdRange RespiratoryRate { get; set; } = new()
    {
        CriticalLow = 8, WarningLow = 12, WarningHigh = 20, CriticalHigh = 30
    };
}

public sealed class ThresholdRange
{
    public double? CriticalLow { get; set; }
    public double? WarningLow { get; set; }
    public double? WarningHigh { get; set; }
    public double? CriticalHigh { get; set; }

    public ThresholdViolation? Evaluate(double value)
    {
        if (CriticalHigh is { } criticalHigh && value > criticalHigh)
            return new ThresholdViolation(VitalStatus.Critical, criticalHigh, IsAboveLimit: true);

        if (CriticalLow is { } criticalLow && value < criticalLow)
            return new ThresholdViolation(VitalStatus.Critical, criticalLow, IsAboveLimit: false);

        if (WarningHigh is { } warningHigh && value > warningHigh)
            return new ThresholdViolation(VitalStatus.Warning, warningHigh, IsAboveLimit: true);

        if (WarningLow is { } warningLow && value < warningLow)
            return new ThresholdViolation(VitalStatus.Warning, warningLow, IsAboveLimit: false);

        return null;
    }
}

public readonly record struct ThresholdViolation(VitalStatus Status, double Limit, bool IsAboveLimit);
