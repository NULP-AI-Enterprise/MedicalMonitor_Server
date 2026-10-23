using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using PatientMonitoring.Api.Contracts;

namespace PatientMonitoring.Api.Services.Auth;

public sealed class AuthService(IOptions<JwtOptions> options, ILogger<AuthService> logger) : IAuthService
{
    private readonly JwtOptions _options = options.Value;

    // Демо-облікові записи медичного персоналу
    private static readonly Dictionary<string, (string Password, string Role)> StaffAccounts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["doctor"] = ("doctor123", Roles.Doctor),
        ["nurse"] = ("nurse123", Roles.Nurse),
        ["admin"] = ("admin123", Roles.Admin)
    };

    public AuthResponse? AuthenticateStaff(LoginRequest request)
    {
        var username = request.Username.Trim();
        if (StaffAccounts.TryGetValue(username, out var account) && account.Password == request.Password)
        {
            logger.LogInformation("Staff user {Username} authenticated as {Role}", username, account.Role);
            return GenerateCustomToken(username, account.Role);
        }

        // Для зручності тестування: будь-який логін з префіксом doctor_ або nurse_
        if (username.StartsWith("doctor_", StringComparison.OrdinalIgnoreCase))
        {
            return GenerateCustomToken(username, Roles.Doctor);
        }

        if (username.StartsWith("nurse_", StringComparison.OrdinalIgnoreCase))
        {
            return GenerateCustomToken(username, Roles.Nurse);
        }

        logger.LogWarning("Failed login attempt for user {Username}", username);
        return null;
    }

    public AuthResponse GenerateDeviceToken(DeviceTokenRequest request)
    {
        var deviceId = request.DeviceId.Trim();
        var claims = new Dictionary<string, string>
        {
            ["device_id"] = deviceId
        };

        if (request.PatientId is { } pid)
        {
            claims["patient_id"] = pid.ToString();
        }

        logger.LogInformation("Generated device token for {DeviceId}", deviceId);
        return GenerateCustomToken($"device:{deviceId}", Roles.Device, claims);
    }

    public AuthResponse GenerateCustomToken(string subject, string role, IDictionary<string, string>? customClaims = null)
    {
        var expiresAt = DateTime.UtcNow.AddMinutes(_options.ExpirationMinutes);
        var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SecretKey));
        var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, subject),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new(ClaimTypes.Name, subject),
            new(ClaimTypes.Role, role)
        };

        if (customClaims is not null)
        {
            foreach (var (k, v) in customClaims)
            {
                claims.Add(new Claim(k, v));
            }
        }

        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Expires = expiresAt,
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            SigningCredentials = credentials
        };

        var tokenHandler = new JwtSecurityTokenHandler();
        var token = tokenHandler.CreateToken(tokenDescriptor);
        var tokenString = tokenHandler.WriteToken(token);

        return new AuthResponse(tokenString, subject, role, expiresAt);
    }
}
