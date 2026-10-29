namespace PatientMonitoring.Api.Models;

public class Patient
{
    public Guid Id { get; set; }
    public required string FullName { get; set; }
    public DateOnly? DateOfBirth { get; set; }
    public string? Ward { get; set; }
    public string? Bed { get; set; }
    public string PatientType { get; set; } = "Adu";

    // The patient ID the station and the gateway use (e.g. "P001"). Null for records that never had one.
    public string? StationPatientId { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }

    public ICollection<VitalSign> VitalSigns { get; set; } = [];
}
