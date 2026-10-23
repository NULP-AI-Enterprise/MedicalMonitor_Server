using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Monitoring.Shared.DTOs;

namespace PatientMonitoring.IntegrationTests;

public class CentralStationContractApiIntegrationTests : IClassFixture<PatientMonitoringWebApplicationFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly HttpClient _client;
    private readonly PatientMonitoringWebApplicationFactory _factory;

    public CentralStationContractApiIntegrationTests(PatientMonitoringWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetHealth_ReturnsHealthyStatusAndCounts()
    {
        // Act
        var response = await _client.GetAsync("/api/v1/health");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadFromJsonAsync<HealthResponse>();
        Assert.NotNull(content);
        Assert.Equal("Healthy", content.Status);
        Assert.True(content.ActivePatients >= 0);
    }

    [Fact]
    public async Task GetPatients_ReturnsPatientListOrderedByBedNumber()
    {
        // Act
        var response = await _client.GetAsync("/api/v1/patients");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var patients = await response.Content.ReadFromJsonAsync<List<PatientInfoDto>>();
        Assert.NotNull(patients);
        Assert.NotEmpty(patients);

        // Verify sorted by bed number
        var beds = patients.Select(p => p.BedNumber).ToList();
        var sorted = beds.OrderBy(b => b).ToList();
        Assert.Equal(sorted, beds);
    }

    [Fact]
    public async Task PatientCrudLifecycle_WorksCorrectly()
    {
        var testId = $"TEST_{Guid.NewGuid():N}";
        var newPatient = new PatientInfoDto(testId, "Test Subject", 99, "Testing Ward", "Adu");

        // 1. Create (POST)
        var createResponse = await _client.PostAsJsonAsync("/api/v1/patients", newPatient);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<PatientInfoDto>();
        Assert.NotNull(created);
        Assert.Equal(testId, created.PatientId);
        Assert.Equal("Test Subject", created.PatientName);

        // 2. Get by Id (GET)
        var getResponse = await _client.GetAsync($"/api/v1/patients/{testId}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var fetched = await getResponse.Content.ReadFromJsonAsync<PatientInfoDto>();
        Assert.NotNull(fetched);
        Assert.Equal(testId, fetched.PatientId);

        // 3. Update (PUT)
        var updatedPatient = newPatient with { PatientName = "Updated Subject", Diagnosis = "Updated Ward" };
        var putResponse = await _client.PutAsJsonAsync($"/api/v1/patients/{testId}", updatedPatient);
        Assert.Equal(HttpStatusCode.OK, putResponse.StatusCode);
        var updated = await putResponse.Content.ReadFromJsonAsync<PatientInfoDto>();
        Assert.NotNull(updated);
        Assert.Equal("Updated Subject", updated.PatientName);

        // 4. Delete (DELETE)
        var deleteResponse = await _client.DeleteAsync($"/api/v1/patients/{testId}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        // 5. Verify deleted
        var getAfterDelete = await _client.GetAsync($"/api/v1/patients/{testId}");
        Assert.Equal(HttpStatusCode.NotFound, getAfterDelete.StatusCode);
    }

    [Fact]
    public async Task IngestTelemetry_AndAlertReconcile_WorksCorrectly()
    {
        var patientId = "P001";
        // Critical high heart rate: 165 bpm (threshold > 150)
        var vitals = new VitalsDto(
            HeartRate: 165,
            SpO2: 98,
            Nibp: new BloodPressureDto(120, 80, 93),
            RespiratoryRate: 16,
            Temperature: 36.6,
            PulseRate: 165
        );

        var telemetry = new MonitorUpdateDto(
            PatientId: patientId,
            PatientName: "Smith, John",
            BedNumber: 1,
            Vitals: vitals,
            Waveforms: Array.Empty<WaveformSampleDto>(),
            Timestamp: DateTimeOffset.UtcNow
        );

        // Ingest telemetry
        var ingestResponse = await _client.PostAsJsonAsync("/api/v1/ingestion/telemetry", telemetry);
        Assert.Equal(HttpStatusCode.Accepted, ingestResponse.StatusCode);

        // Latest vitals should now be updated
        var latestVitalsResponse = await _client.GetAsync($"/api/v1/patients/{patientId}/vitals/latest");
        Assert.Equal(HttpStatusCode.OK, latestVitalsResponse.StatusCode);
        var latestVitals = await latestVitalsResponse.Content.ReadFromJsonAsync<VitalsDto>();
        Assert.NotNull(latestVitals);
        Assert.Equal(165, latestVitals.HeartRate);

        // Active alerts should include the HR critical breach
        var alertsResponse = await _client.GetAsync("/api/v1/alerts/active");
        Assert.Equal(HttpStatusCode.OK, alertsResponse.StatusCode);
        var alerts = await alertsResponse.Content.ReadFromJsonAsync<List<AlertDto>>(JsonOptions);
        Assert.NotNull(alerts);

        var hrAlert = alerts.FirstOrDefault(a => a.PatientId == patientId && a.VitalSign == VitalSignType.HeartRate);
        Assert.NotNull(hrAlert);
        Assert.Equal(AlertSeverity.Critical, hrAlert.Severity);
        Assert.False(hrAlert.IsAcknowledged);

        // Acknowledge the alert
        var ackResponse = await _client.PostAsync($"/api/v1/alerts/{hrAlert.AlertId}/acknowledge", null);
        Assert.Equal(HttpStatusCode.OK, ackResponse.StatusCode);
        var ackedAlert = await ackResponse.Content.ReadFromJsonAsync<AlertDto>(JsonOptions);
        Assert.NotNull(ackedAlert);
        Assert.True(ackedAlert.IsAcknowledged);
    }

    [Fact]
    public async Task VitalsHubNegotiate_ReturnsValidNegotiationResponse()
    {
        var response = await _client.PostAsync("/hubs/vitals/negotiate?negotiateVersion=1", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("connectionId", json);
        Assert.Contains("availableTransports", json);
    }

    [Fact]
    public async Task SignalRClient_CanConnect_AndInvokeContractMethods()
    {
        var addedPatients = new List<PatientInfoDto>();

        var connection = new Microsoft.AspNetCore.SignalR.Client.HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, Monitoring.Shared.HubMethods.Route), options =>
            {
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
            })
            .AddJsonProtocol(options =>
            {
                options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter());
            })
            .Build();

        connection.On<PatientInfoDto>(Monitoring.Shared.HubMethods.PatientAdded, p =>
        {
            lock (addedPatients) addedPatients.Add(p);
        });

        await connection.StartAsync();

        // 1. On connect, server should push PatientAdded messages
        await Task.Delay(100);
        lock (addedPatients)
        {
            Assert.NotEmpty(addedPatients);
        }

        // 2. Call RPC GetPatients
        var patients = await connection.InvokeAsync<PatientInfoDto[]>(Monitoring.Shared.HubMethods.GetPatients);
        Assert.NotNull(patients);
        Assert.NotEmpty(patients);

        // 3. Call RPC GetActiveAlerts
        var alerts = await connection.InvokeAsync<AlertDto[]>(Monitoring.Shared.HubMethods.GetActiveAlerts);
        Assert.NotNull(alerts);

        await connection.StopAsync();
    }

    private sealed record HealthResponse(string Status, DateTime Timestamp, int ActivePatients, int ConnectedStations);
}
