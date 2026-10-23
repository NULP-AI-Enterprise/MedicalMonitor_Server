using PatientMonitoring.Api.Contracts;
using PatientMonitoring.Api.Models;

namespace PatientMonitoring.Api.Services;

internal static class Mapping
{
    public static VitalSignsResponse ToResponse(this VitalSign vitals, IReadOnlyList<VitalAlert> alerts, string? patientFullName = null) => new(
        vitals.Id,
        vitals.PatientId,
        vitals.RecordedAt,
        vitals.ReceivedAt,
        vitals.DeviceId,
        vitals.HeartRate,
        vitals.SystolicBloodPressure,
        vitals.DiastolicBloodPressure,
        vitals.OxygenSaturation,
        vitals.Temperature,
        vitals.RespiratoryRate,
        vitals.Status,
        alerts,
        patientFullName ?? vitals.Patient?.FullName);

    public static VitalSignsResponse ToResponse(this VitalSign vitals, IVitalSignsAnalyzer analyzer, string? patientFullName = null)
        => vitals.ToResponse(analyzer.Analyze(vitals).Alerts, patientFullName);

    public static PatientResponse ToResponse(this Patient patient, VitalSignsResponse? latestVitals) => new(
        patient.Id,
        patient.FullName,
        patient.DateOfBirth,
        patient.Ward,
        patient.Bed,
        patient.IsActive,
        patient.CreatedAt,
        latestVitals);
}
