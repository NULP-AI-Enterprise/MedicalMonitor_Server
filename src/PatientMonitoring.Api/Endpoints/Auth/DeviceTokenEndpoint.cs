using FastEndpoints;
using PatientMonitoring.Api.Contracts;
using PatientMonitoring.Api.Services.Auth;

namespace PatientMonitoring.Api.Endpoints.Auth;

public sealed class DeviceTokenEndpoint(IAuthService authService)
    : Endpoint<DeviceTokenRequest, AuthResponse>
{
    public override void Configure()
    {
        Post("/api/auth/device-token");
        AllowAnonymous();
    }

    public override async Task HandleAsync(DeviceTokenRequest req, CancellationToken ct)
    {
        var response = authService.GenerateDeviceToken(req);
        await Send.OkAsync(response, ct);
    }
}
