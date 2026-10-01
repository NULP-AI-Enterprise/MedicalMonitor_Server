using System.Net;

namespace PatientMonitoring.IntegrationTests;

public class HealthChecksIntegrationTests : IClassFixture<PatientMonitoringWebApplicationFactory>
{
    private readonly HttpClient _client;

    public HealthChecksIntegrationTests(PatientMonitoringWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Healthz_ReturnsHealthy()
    {
        var response = await _client.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        Assert.Equal("Healthy", content);
    }

    [Fact]
    public async Task ReadyHealthCheck_ReturnsHealthy()
    {
        var response = await _client.GetAsync("/healthz/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        Assert.Equal("Healthy", content);
    }

    [Fact]
    public async Task LiveHealthCheck_ReturnsHealthy()
    {
        var response = await _client.GetAsync("/healthz/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        Assert.Equal("Healthy", content);
    }
}
