namespace Monitoring.Shared.DTOs;

/// <summary>
/// Severity level for vital sign alerts.
/// </summary>
public enum AlertSeverity
{
    /// <summary>Vital sign approaching abnormal range.</summary>
    Warning,

    /// <summary>Vital sign in dangerous range — immediate attention required.</summary>
    Critical
}

/// <summary>
/// Type of vital sign being monitored.
/// </summary>
public enum VitalSignType
{
    HeartRate,
    SpO2,
    Systolic,
    Diastolic,
    RespiratoryRate,
    Temperature
}

/// <summary>
/// Type of waveform trace displayed on the monitor.
/// </summary>
public enum WaveformType
{
    /// <summary>ECG Lead II (primary).</summary>
    ECG_II,

    /// <summary>ECG Lead I (secondary).</summary>
    ECG_I,

    /// <summary>Photoplethysmogram (SpO2 pulse waveform).</summary>
    Pleth,

    /// <summary>Impedance respiratory waveform.</summary>
    Respiration
}

/// <summary>
/// Short clinical labels for vital signs, shared so the web and desktop
/// stations always render an alert against the same wording.
/// </summary>
public static class VitalSignLabels
{
    public static string Short(VitalSignType type) => type switch
    {
        VitalSignType.HeartRate => "HR",
        VitalSignType.SpO2 => "SpO2",
        VitalSignType.Systolic => "NIBP Sys",
        VitalSignType.Diastolic => "NIBP Dia",
        VitalSignType.RespiratoryRate => "RESP",
        VitalSignType.Temperature => "TEMP",
        _ => type.ToString()
    };

    public static string Unit(VitalSignType type) => type switch
    {
        VitalSignType.HeartRate => "bpm",
        VitalSignType.SpO2 => "%",
        VitalSignType.Systolic or VitalSignType.Diastolic => "mmHg",
        VitalSignType.RespiratoryRate => "rpm",
        VitalSignType.Temperature => "°C",
        _ => string.Empty
    };
}
