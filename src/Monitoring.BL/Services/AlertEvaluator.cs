namespace Monitoring.BL.Services;

using System;
using System.Collections.Generic;
using Monitoring.Shared;
using Monitoring.Shared.DTOs;

/// <summary>
/// Pure, stateless threshold evaluation. Produces the set of alerts a patient's
/// vitals *should* currently have; reconciling that against what is already active
/// (raise / clear / escalate) is <see cref="AlertStateService"/>'s job.
/// </summary>
public static class AlertEvaluator
{
    public static List<AlertDto> Evaluate(string patientId, VitalsDto vitals)
    {
        var alerts = new List<AlertDto>();

        CheckThreshold(alerts, patientId, VitalSignType.HeartRate, vitals.HeartRate,
            VitalThresholds.HeartRateCriticalLow, VitalThresholds.HeartRateWarningLow,
            VitalThresholds.HeartRateWarningHigh, VitalThresholds.HeartRateCriticalHigh);

        // SpO2 has no upper bound — 100% saturation is never an alarm condition.
        CheckThreshold(alerts, patientId, VitalSignType.SpO2, vitals.SpO2,
            VitalThresholds.SpO2CriticalLow, VitalThresholds.SpO2WarningLow,
            double.PositiveInfinity, double.PositiveInfinity);

        if (vitals.Nibp is not null)
        {
            CheckThreshold(alerts, patientId, VitalSignType.Systolic, vitals.Nibp.Systolic,
                VitalThresholds.SystolicCriticalLow, VitalThresholds.SystolicWarningLow,
                VitalThresholds.SystolicWarningHigh, VitalThresholds.SystolicCriticalHigh);

            CheckThreshold(alerts, patientId, VitalSignType.Diastolic, vitals.Nibp.Diastolic,
                VitalThresholds.DiastolicCriticalLow, VitalThresholds.DiastolicWarningLow,
                VitalThresholds.DiastolicWarningHigh, VitalThresholds.DiastolicCriticalHigh);
        }

        CheckThreshold(alerts, patientId, VitalSignType.RespiratoryRate, vitals.RespiratoryRate,
            VitalThresholds.RespRateCriticalLow, VitalThresholds.RespRateWarningLow,
            VitalThresholds.RespRateWarningHigh, VitalThresholds.RespRateCriticalHigh);

        CheckThreshold(alerts, patientId, VitalSignType.Temperature, vitals.Temperature,
            VitalThresholds.TempCriticalLow, VitalThresholds.TempWarningLow,
            VitalThresholds.TempWarningHigh, VitalThresholds.TempCriticalHigh);

        return alerts;
    }

    private static void CheckThreshold(List<AlertDto> alerts, string patientId, VitalSignType type, double value,
        double criticalLow, double warningLow, double warningHigh, double criticalHigh)
    {
        if (value < criticalLow)
            alerts.Add(CreateAlert(patientId, type, value, AlertSeverity.Critical, "critically low"));
        else if (value > criticalHigh)
            alerts.Add(CreateAlert(patientId, type, value, AlertSeverity.Critical, "critically high"));
        else if (value < warningLow)
            alerts.Add(CreateAlert(patientId, type, value, AlertSeverity.Warning, "low"));
        else if (value > warningHigh)
            alerts.Add(CreateAlert(patientId, type, value, AlertSeverity.Warning, "high"));
    }

    private static AlertDto CreateAlert(string patientId, VitalSignType type, double value, AlertSeverity severity, string direction)
    {
        // Temperature is the only fractional vital; everything else reads as a whole number.
        var formatted = type == VitalSignType.Temperature ? value.ToString("F1") : Math.Round(value).ToString("F0");
        var unit = VitalSignLabels.Unit(type);

        return new AlertDto(
            AlertId: AlertDto.BuildId(patientId, type),
            PatientId: patientId,
            VitalSign: type,
            Severity: severity,
            Message: $"{VitalSignLabels.Short(type)} {direction}: {formatted} {unit}".TrimEnd(),
            Value: value,
            Timestamp: DateTimeOffset.UtcNow
        );
    }
}
