using PatientMonitoring.Api.Contracts;

namespace PatientMonitoring.Api.Services.Auth;

public interface IAuthService
{
    AuthResponse? AuthenticateStaff(LoginRequest request);

    AuthResponse GenerateDeviceToken(DeviceTokenRequest request);

    AuthResponse GenerateCustomToken(string subject, string role, IDictionary<string, string>? customClaims = null);
}
