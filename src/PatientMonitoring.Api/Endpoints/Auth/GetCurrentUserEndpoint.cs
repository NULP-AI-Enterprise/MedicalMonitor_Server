using System.Security.Claims;
using FastEndpoints;
using PatientMonitoring.Api.Contracts;

namespace PatientMonitoring.Api.Endpoints.Auth;

public sealed class GetCurrentUserEndpoint : EndpointWithoutRequest<UserInfoResponse>
{
    public override void Configure()
    {
        Get("/api/auth/me");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var user = HttpContext.User;
        var username = user.Identity?.Name ?? "Anonymous";
        var role = user.FindFirstValue(ClaimTypes.Role) ?? "None";
        var claims = user.Claims
            .GroupBy(c => c.Type)
            .ToDictionary(g => g.Key, g => string.Join(", ", g.Select(c => c.Value)));

        await Send.OkAsync(new UserInfoResponse(
            username,
            role,
            user.Identity?.IsAuthenticated ?? false,
            claims), ct);
    }
}
