using System.IdentityModel.Tokens.Jwt;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PatientMonitoring.Api.Contracts;
using PatientMonitoring.Api.Services.Auth;

namespace PatientMonitoring.UnitTests;

public class AuthServiceTests
{
    private readonly AuthService _authService;

    public AuthServiceTests()
    {
        var options = Options.Create(new JwtOptions
        {
            SecretKey = "TestSuperSecretKey_ForUnitTests_AtLeast32BytesLong!",
            Issuer = "PatientMonitoringTest",
            Audience = "PatientMonitoringClientsTest",
            ExpirationMinutes = 60
        });

        _authService = new AuthService(options, NullLogger<AuthService>.Instance);
    }

    [Theory]
    [InlineData("doctor", "doctor123", Roles.Doctor)]
    [InlineData("nurse", "nurse123", Roles.Nurse)]
    [InlineData("admin", "admin123", Roles.Admin)]
    public void AuthenticateStaff_WhenValidCredentials_ReturnsTokenWithCorrectRole(string user, string pass, string expectedRole)
    {
        // Act
        var result = _authService.AuthenticateStaff(new LoginRequest { Username = user, Password = pass });

        // Assert
        Assert.NotNull(result);
        Assert.Equal(expectedRole, result.Role);
        Assert.False(string.IsNullOrWhiteSpace(result.Token));

        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(result.Token);
        Assert.Equal(user, jwt.Subject);
        Assert.Equal("PatientMonitoringTest", jwt.Issuer);
    }

    [Fact]
    public void AuthenticateStaff_WhenInvalidPassword_ReturnsNull()
    {
        // Act
        var result = _authService.AuthenticateStaff(new LoginRequest { Username = "doctor", Password = "wrongpassword" });

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void GenerateDeviceToken_ReturnsValidTokenWithDeviceRoleAndClaims()
    {
        // Arrange
        var patientId = Guid.NewGuid();
        var request = new DeviceTokenRequest { DeviceId = "sensor-bed-101", PatientId = patientId };

        // Act
        var result = _authService.GenerateDeviceToken(request);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(Roles.Device, result.Role);
        Assert.Equal("device:sensor-bed-101", result.Username);

        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(result.Token);
        Assert.Contains(jwt.Claims, c => c.Type == "device_id" && c.Value == "sensor-bed-101");
        Assert.Contains(jwt.Claims, c => c.Type == "patient_id" && c.Value == patientId.ToString());
    }
}
