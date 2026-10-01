using FastEndpoints;
using Microsoft.AspNetCore.Http;
using PatientMonitoring.Api.Contracts;
using PatientMonitoring.Api.Services.Auth;

namespace PatientMonitoring.Api.Endpoints.Auth;

public sealed class LoginEndpoint(IAuthService authService)
    : Endpoint<LoginRequest, AuthResponse>
{
    public override void Configure()
    {
        Post("/api/auth/login");
        AllowAnonymous();
    }

    public override async Task HandleAsync(LoginRequest req, CancellationToken ct)
    {
        var response = authService.AuthenticateStaff(req);
        if (response is null)
        {
            await Send.ResultAsync(TypedResults.Json(
                new { message = "Невірний логін або пароль. Спробуйте: doctor / doctor123 або nurse / nurse123" },
                statusCode: StatusCodes.Status401Unauthorized));
            return;
        }

        await Send.OkAsync(response, ct);
    }
}
