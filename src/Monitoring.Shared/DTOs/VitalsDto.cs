namespace Monitoring.Shared.DTOs;

/// <summary>
/// Current vital sign readings for a patient.
/// </summary>
/// <param name="HeartRate">Heart rate in bpm (normal: 60–100).</param>
/// <param name="SpO2">Oxygen saturation percentage (normal: 95–100).</param>
/// <param name="Nibp">Non-Invasive Blood Pressure reading.</param>
/// <param name="RespiratoryRate">Respiratory rate in breaths/min (normal: 12–20).</param>
/// <param name="Temperature">Body temperature in °C (normal: 36.1–37.2).</param>
/// <param name="PulseRate">Pulse rate from SpO2 sensor in bpm.</param>
public record VitalsDto(
    int HeartRate,
    int SpO2,
    BloodPressureDto Nibp,
    int RespiratoryRate,
    double Temperature,
    int PulseRate
);
