using PatientMonitoring.Api.Models;

namespace PatientMonitoring.Api.Services;

/// <summary>
/// Пороги норми для показників. Значення — фіксовані клінічні пороги контракту
/// (Monitoring.Shared.VitalThresholds), ті самі, що використовує станція. Їх не перевизначають через
/// appsettings.json, щоб старий API і станція не давали різних тривог для одних і тих самих даних.
/// </summary>
public sealed class VitalThresholds
{
    public const string SectionName = "VitalThresholds";

    public ThresholdRange HeartRate { get; set; } = new()
    {
        CriticalLow = Monitoring.Shared.VitalThresholds.HeartRateCriticalLow,
        WarningLow = Monitoring.Shared.VitalThresholds.HeartRateWarningLow,
        WarningHigh = Monitoring.Shared.VitalThresholds.HeartRateWarningHigh,
        CriticalHigh = Monitoring.Shared.VitalThresholds.HeartRateCriticalHigh
    };

    public ThresholdRange SystolicBloodPressure { get; set; } = new()
    {
        CriticalLow = Monitoring.Shared.VitalThresholds.SystolicCriticalLow,
        WarningLow = Monitoring.Shared.VitalThresholds.SystolicWarningLow,
        WarningHigh = Monitoring.Shared.VitalThresholds.SystolicWarningHigh,
        CriticalHigh = Monitoring.Shared.VitalThresholds.SystolicCriticalHigh
    };

    public ThresholdRange DiastolicBloodPressure { get; set; } = new()
    {
        CriticalLow = Monitoring.Shared.VitalThresholds.DiastolicCriticalLow,
        WarningLow = Monitoring.Shared.VitalThresholds.DiastolicWarningLow,
        WarningHigh = Monitoring.Shared.VitalThresholds.DiastolicWarningHigh,
        CriticalHigh = Monitoring.Shared.VitalThresholds.DiastolicCriticalHigh
    };

    public ThresholdRange OxygenSaturation { get; set; } = new()
    {
        CriticalLow = Monitoring.Shared.VitalThresholds.SpO2CriticalLow,
        WarningLow = Monitoring.Shared.VitalThresholds.SpO2WarningLow
    };

    public ThresholdRange Temperature { get; set; } = new()
    {
        CriticalLow = Monitoring.Shared.VitalThresholds.TempCriticalLow,
        WarningLow = Monitoring.Shared.VitalThresholds.TempWarningLow,
        WarningHigh = Monitoring.Shared.VitalThresholds.TempWarningHigh,
        CriticalHigh = Monitoring.Shared.VitalThresholds.TempCriticalHigh
    };

    public ThresholdRange RespiratoryRate { get; set; } = new()
    {
        CriticalLow = Monitoring.Shared.VitalThresholds.RespRateCriticalLow,
        WarningLow = Monitoring.Shared.VitalThresholds.RespRateWarningLow,
        WarningHigh = Monitoring.Shared.VitalThresholds.RespRateWarningHigh,
        CriticalHigh = Monitoring.Shared.VitalThresholds.RespRateCriticalHigh
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
