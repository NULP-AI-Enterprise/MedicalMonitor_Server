using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using PatientMonitoring.Api.Contracts;

namespace PatientMonitoring.IntegrationTests;

public class AuthEndpointsIntegrationTests : IClassFixture<PatientMonitoringWebApplicationFactory>
{
    private readonly HttpClient _client;

    public AuthEndpointsIntegrationTests(PatientMonitoringWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Login_WithValidCredentials_ReturnsTokenAndRole()
    {
        var request = new LoginRequest { Username = "doctor", Password = "doctor123" };
        var response = await _client.PostAsJsonAsync("/api/auth/login", request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var authResponse = await response.Content.ReadFromJsonAsync<AuthResponse>();

        Assert.NotNull(authResponse);
        Assert.NotEmpty(authResponse.Token);
        Assert.Equal("doctor", authResponse.Username);
        Assert.Equal(Roles.Doctor, authResponse.Role);
    }

    [Fact]
    public async Task Login_WithInvalidPassword_ReturnsUnauthorized()
    {
        var request = new LoginRequest { Username = "doctor", Password = "WrongPassword" };
        var response = await _client.PostAsJsonAsync("/api/auth/login", request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task DeviceToken_GeneratesTokenForDevice()
    {
        var request = new DeviceTokenRequest { DeviceId = "MONITOR-ICU-42" };
        var response = await _client.PostAsJsonAsync("/api/auth/device-token", request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var authResponse = await response.Content.ReadFromJsonAsync<AuthResponse>();

        Assert.NotNull(authResponse);
        Assert.NotEmpty(authResponse.Token);
        Assert.Equal("device:MONITOR-ICU-42", authResponse.Username);
        Assert.Equal(Roles.Device, authResponse.Role);
    }

    [Fact]
    public async Task Me_WithoutToken_ReturnsUnauthorized()
    {
        var response = await _client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Me_WithBearerToken_ReturnsUserInfo()
    {
        // Login first
        var loginReq = new LoginRequest { Username = "nurse", Password = "nurse123" };
        var loginRes = await _client.PostAsJsonAsync("/api/auth/login", loginReq);
        var auth = await loginRes.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.NotNull(auth);

        // Call /me with Bearer token
        using var meRequest = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        meRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);

        var meResponse = await _client.SendAsync(meRequest);
        Assert.Equal(HttpStatusCode.OK, meResponse.StatusCode);

        var meData = await meResponse.Content.ReadFromJsonAsync<UserInfoResponse>();
        Assert.NotNull(meData);
        Assert.Equal("nurse", meData.Username);
        Assert.Equal(Roles.Nurse, meData.Role);
    }
}
