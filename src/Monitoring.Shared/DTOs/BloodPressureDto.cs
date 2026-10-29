namespace Monitoring.Shared.DTOs;

/// <summary>
/// Non-Invasive Blood Pressure reading.
/// </summary>
/// <param name="Systolic">Systolic pressure in mmHg (normal: 90–120).</param>
/// <param name="Diastolic">Diastolic pressure in mmHg (normal: 60–80).</param>
/// <param name="MeanArterialPressure">MAP in mmHg, calculated as (Systolic + 2×Diastolic) / 3.</param>
public record BloodPressureDto(
    int Systolic,
    int Diastolic,
    int MeanArterialPressure
);
