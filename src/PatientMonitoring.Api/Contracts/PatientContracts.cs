using System.ComponentModel.DataAnnotations;

namespace PatientMonitoring.Api.Contracts;

public sealed record CreatePatientRequest
{
    [Required, MaxLength(200)]
    public required string FullName { get; init; }

    public DateOnly? DateOfBirth { get; init; }

    [MaxLength(50)]
    public string? Ward { get; init; }

    [MaxLength(20)]
    public string? Bed { get; init; }
}

public sealed record UpdatePatientRequest
{
    [Required, MaxLength(200)]
    public required string FullName { get; init; }

    public DateOnly? DateOfBirth { get; init; }

    [MaxLength(50)]
    public string? Ward { get; init; }

    [MaxLength(20)]
    public string? Bed { get; init; }

    public bool IsActive { get; init; } = true;
}

public sealed record PatientResponse(
    Guid Id,
    string FullName,
    DateOnly? DateOfBirth,
    string? Ward,
    string? Bed,
    bool IsActive,
    DateTime CreatedAt,
    VitalSignsResponse? LatestVitals);
