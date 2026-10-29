namespace Monitoring.Shared.DTOs;

/// <summary>
/// Patient identification and metadata.
/// </summary>
/// <param name="PatientId">Unique patient identifier.</param>
/// <param name="PatientName">Display name.</param>
/// <param name="BedNumber">Bed/station number.</param>
/// <param name="Diagnosis">Optional primary diagnosis.</param>
/// <param name="PatientType">Patient category: "Adu" (Adult), "Ped" (Pediatric), "Neo" (Neonatal).</param>
public record PatientInfoDto(
    string PatientId,
    string PatientName,
    int BedNumber,
    string? Diagnosis,
    string PatientType = "Adu"
);
