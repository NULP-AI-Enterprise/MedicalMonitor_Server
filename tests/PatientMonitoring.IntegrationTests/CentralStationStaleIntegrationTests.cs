using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Monitoring.Shared;
using Monitoring.Shared.DTOs;

namespace PatientMonitoring.IntegrationTests;

public class CentralStationStaleIntegrationTests : IClassFixture<PatientMonitoringWebApplicationFactory>
{
    private readonly PatientMonitoringWebApplicationFactory _factory;

    public CentralStationStaleIntegrationTests(PatientMonitoringWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task SilentMonitor_IsRemovedFromStation_AndReturnsWhenItReportsAgain()
    {
        using var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["CentralStation:DeviceStaleAfterSeconds"] = "1"
                })));
        var client = factory.CreateClient();
        var patientId = $"STALE_{Guid.NewGuid():N}";
        var removed = new List<string>();
        var added = new List<PatientInfoDto>();

        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, HubMethods.Route), options =>
            {
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
            })
            .Build();
        connection.On<string>(HubMethods.PatientRemoved, id => { lock (removed) removed.Add(id); });
        connection.On<PatientInfoDto>(HubMethods.PatientAdded, p => { lock (added) added.Add(p); });

        await connection.StartAsync();
        try
        {
            await PostReadingAsync(client, patientId);

            var gone = await WaitForAsync(() => { lock (removed) return removed.Contains(patientId) ? patientId : null; }, 6000);
            Assert.NotNull(gone);

            await PostReadingAsync(client, patientId);

            var back = await WaitForAsync(() => { lock (added) return added.LastOrDefault(p => p.PatientId == patientId); }, 3000);
            Assert.NotNull(back);
        }
        finally
        {
            await connection.StopAsync();
        }
    }

    private static async Task PostReadingAsync(HttpClient client, string patientId)
    {
        var telemetry = new MonitorUpdateDto(
            patientId,
            "Stale Test",
            0,
            new VitalsDto(75, 98, new BloodPressureDto(120, 80, 93), 16, 36.6, 75),
            Array.Empty<WaveformSampleDto>(),
            DateTimeOffset.UtcNow);

        var response = await client.PostAsJsonAsync("/api/v1/ingestion/telemetry", telemetry);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    private static async Task<T?> WaitForAsync<T>(Func<T?> probe, int timeoutMs) where T : class
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var value = probe();
            if (value is not null) return value;
            await Task.Delay(50);
        }

        return null;
    }
}
