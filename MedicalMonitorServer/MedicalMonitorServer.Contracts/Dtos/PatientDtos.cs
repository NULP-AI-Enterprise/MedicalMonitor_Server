namespace MedicalMonitorServer.Contracts.Dtos;

public sealed class PatientDataDto
{
    public string PatientId { get; init; } = string.Empty;

    public string? Name { get; init; }

    public IReadOnlyDictionary<string, string> Values { get; init; } =
        new Dictionary<string, string>();
}

public sealed class PatientInfoDto
{
    public string PatientId { get; init; } = string.Empty;

    public string LastName { get; init; } = string.Empty;

    public string FirstName { get; init; } = string.Empty;

    public string FullName { get; init; } = string.Empty;

    public DateOnly? DateOfBirth { get; init; }

    public string Sex { get; init; } = string.Empty;

    public string PatientClass { get; init; } = string.Empty;

    public PatientTypeDto PatientType { get; init; }

    public string Department { get; init; } = string.Empty;

    public string BedNumber { get; init; } = string.Empty;

    public string BedLabel { get; init; } = string.Empty;

    public string MonitorIp { get; init; } = string.Empty;

    public int? MonitorPort { get; init; }

    public bool? Admitted { get; init; }

    public DateOnly? AdmitDate { get; init; }

    public double? HeightCm { get; init; }

    public double? WeightKg { get; init; }

    public string BloodType { get; init; } = string.Empty;

    public bool? Paced { get; init; }
}
