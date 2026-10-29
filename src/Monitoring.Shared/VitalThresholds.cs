namespace Monitoring.Shared;

/// <summary>
/// Clinical threshold ranges for vital sign alerting.
/// Values based on standard adult monitoring defaults.
/// </summary>
public static class VitalThresholds
{
    // ── Heart Rate (bpm) ──────────────────────────────────────
    public const int HeartRateCriticalLow = 40;
    public const int HeartRateWarningLow = 50;
    public const int HeartRateWarningHigh = 110;
    public const int HeartRateCriticalHigh = 150;

    // ── SpO2 (%) ──────────────────────────────────────────────
    public const int SpO2CriticalLow = 88;
    public const int SpO2WarningLow = 92;
    // No high threshold — 100% is always fine

    // ── Systolic BP (mmHg) ────────────────────────────────────
    public const int SystolicCriticalLow = 70;
    public const int SystolicWarningLow = 85;
    public const int SystolicWarningHigh = 140;
    public const int SystolicCriticalHigh = 180;

    // ── Diastolic BP (mmHg) ───────────────────────────────────
    public const int DiastolicCriticalLow = 45;
    public const int DiastolicWarningLow = 55;
    public const int DiastolicWarningHigh = 95;
    public const int DiastolicCriticalHigh = 110;

    // ── Respiratory Rate (breaths/min) ────────────────────────
    public const int RespRateCriticalLow = 8;
    public const int RespRateWarningLow = 10;
    public const int RespRateWarningHigh = 24;
    public const int RespRateCriticalHigh = 30;

    // ── Temperature (°C) ─────────────────────────────────────
    public const double TempCriticalLow = 35.0;
    public const double TempWarningLow = 36.0;
    public const double TempWarningHigh = 37.8;
    public const double TempCriticalHigh = 39.0;
}
