namespace PatientMonitoring.Api.Services.Auth;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string SecretKey { get; set; } = "PatientMonitoring_VerySecretSecurityKey_AtLeast32BytesLong!_2026";

    public string Issuer { get; set; } = "PatientMonitoring";

    public string Audience { get; set; } = "PatientMonitoringClients";

    public int ExpirationMinutes { get; set; } = 120;
}
