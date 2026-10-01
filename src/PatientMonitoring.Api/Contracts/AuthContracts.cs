using System.ComponentModel.DataAnnotations;

namespace PatientMonitoring.Api.Contracts;

public static class Roles
{
    public const string Doctor = "Doctor";
    public const string Nurse = "Nurse";
    public const string Device = "Device";
    public const string Admin = "Admin";
}

public sealed record LoginRequest
{
    [Required]
    public required string Username { get; init; }

    [Required]
    public required string Password { get; init; }
}

public sealed record DeviceTokenRequest
{
    [Required, MaxLength(100)]
    public required string DeviceId { get; init; }

    public Guid? PatientId { get; init; }
}

public sealed record AuthResponse(
    string Token,
    string Username,
    string Role,
    DateTime ExpiresAt);

public sealed record UserInfoResponse(
    string Username,
    string Role,
    bool IsAuthenticated,
    IReadOnlyDictionary<string, string> Claims);
