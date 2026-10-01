using System.Globalization;
using Microsoft.Extensions.Options;
using PatientMonitoring.Api.Contracts;
using PatientMonitoring.Api.Models;

namespace PatientMonitoring.Api.Services;

public sealed record VitalAnalysis(VitalStatus Status, IReadOnlyList<VitalAlert> Alerts);

public interface IVitalSignsAnalyzer
{
    VitalAnalysis Analyze(VitalSign vitals);
}

public sealed class VitalSignsAnalyzer(IOptions<VitalThresholds> options) : IVitalSignsAnalyzer
{
    private readonly VitalThresholds _thresholds = options.Value;

    public VitalAnalysis Analyze(VitalSign vitals)
    {
        var alerts = new List<VitalAlert>();

        Check(alerts, nameof(vitals.HeartRate), vitals.HeartRate, _thresholds.HeartRate, "Пульс", "уд/хв");
        Check(alerts, nameof(vitals.SystolicBloodPressure), vitals.SystolicBloodPressure, _thresholds.SystolicBloodPressure, "Систолічний тиск", "мм рт. ст.");
        Check(alerts, nameof(vitals.DiastolicBloodPressure), vitals.DiastolicBloodPressure, _thresholds.DiastolicBloodPressure, "Діастолічний тиск", "мм рт. ст.");
        Check(alerts, nameof(vitals.OxygenSaturation), vitals.OxygenSaturation, _thresholds.OxygenSaturation, "SpO₂", "%");
        Check(alerts, nameof(vitals.Temperature), vitals.Temperature, _thresholds.Temperature, "Температура", "°C");
        Check(alerts, nameof(vitals.RespiratoryRate), vitals.RespiratoryRate, _thresholds.RespiratoryRate, "Частота дихання", "/хв");

        var status = alerts.Count == 0 ? VitalStatus.Normal : alerts.Max(a => a.Status);
        return new VitalAnalysis(status, alerts);
    }

    private static void Check(
        List<VitalAlert> alerts,
        string parameter,
        double? value,
        ThresholdRange range,
        string label,
        string unit)
    {
        if (value is not { } actual)
            return;

        if (range.Evaluate(actual) is not { } violation)
            return;

        var direction = violation.IsAboveLimit ? "вище" : "нижче";
        var limitKind = violation.Status == VitalStatus.Critical ? "критичної межі" : "межі попередження";
        var formattedValue = actual.ToString("0.#", CultureInfo.InvariantCulture);
        var formattedLimit = violation.Limit.ToString("0.#", CultureInfo.InvariantCulture);

        alerts.Add(new VitalAlert(
            parameter,
            actual,
            violation.Status,
            $"{label} {formattedValue} {unit} — {direction} {limitKind} ({formattedLimit})"));
    }
}
