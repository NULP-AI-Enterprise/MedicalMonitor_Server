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
    public async Task Swagger_Endpoints_Work()
    {
        // 1. Swagger UI
        var swaggerUiResp = await _client.GetAsync("/swagger/index.html");
        Assert.Equal(HttpStatusCode.OK, swaggerUiResp.StatusCode);
        var html = await swaggerUiResp.Content.ReadAsStringAsync();
        Assert.Contains("swagger", html, StringComparison.OrdinalIgnoreCase);

        // 2. Swagger JSON specification
        var swaggerJsonResp = await _client.GetAsync("/swagger/v1/swagger.json");
        Assert.Equal(HttpStatusCode.OK, swaggerJsonResp.StatusCode);
        var json = await swaggerJsonResp.Content.ReadAsStringAsync();
        Assert.Contains("/api/v1/patients", json);
        Assert.Contains("/api/v1/health", json);
        Assert.Contains("/api/v1/alerts/active", json);
        Assert.Contains("/api/v1/ingestion/telemetry", json);
    }

    [Fact]
    public async Task RootRoute_ServesCentralNurseStationStaticFiles()
    {
        var response = await _client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("Central Nurse Station", content, StringComparison.OrdinalIgnoreCase);

        var appJsResponse = await _client.GetAsync("/app.js");
        Assert.Equal(HttpStatusCode.OK, appJsResponse.StatusCode);
    }

    [Fact]
    public async Task JsonSerialization_StrictCamelCaseAndStringEnums()
    {
        // 1. Health response
        var healthResp = await _client.GetAsync("/api/v1/health");
        var healthJson = await healthResp.Content.ReadAsStringAsync();
        Assert.Contains("\"status\"", healthJson);
        Assert.Contains("\"timestamp\"", healthJson);
        Assert.Contains("\"activePatients\"", healthJson);
        Assert.Contains("\"connectedStations\"", healthJson);
        Assert.DoesNotContain("\"Status\"", healthJson);
        Assert.DoesNotContain("\"ActivePatients\"", healthJson);

        // 2. Patients list response
        var patientsResp = await _client.GetAsync("/api/v1/patients");
        var patientsJson = await patientsResp.Content.ReadAsStringAsync();
        Assert.Contains("\"patientId\"", patientsJson);
        Assert.Contains("\"patientName\"", patientsJson);
        Assert.Contains("\"bedNumber\"", patientsJson);
        Assert.Contains("\"patientType\"", patientsJson);
        Assert.DoesNotContain("\"PatientId\"", patientsJson);
        Assert.DoesNotContain("\"PatientName\"", patientsJson);

        // 3. Vitals response
        var vitalsResp = await _client.GetAsync("/api/v1/patients/P001/vitals/latest");
        var vitalsJson = await vitalsResp.Content.ReadAsStringAsync();
        Assert.Contains("\"heartRate\"", vitalsJson);
        Assert.Contains("\"spO2\"", vitalsJson);
        Assert.Contains("\"nibp\"", vitalsJson);
        Assert.Contains("\"systolic\"", vitalsJson);
        Assert.Contains("\"diastolic\"", vitalsJson);
        Assert.Contains("\"meanArterialPressure\"", vitalsJson);
        Assert.Contains("\"respiratoryRate\"", vitalsJson);
        Assert.Contains("\"temperature\"", vitalsJson);
        Assert.Contains("\"pulseRate\"", vitalsJson);
        Assert.DoesNotContain("\"HeartRate\"", vitalsJson);
        Assert.DoesNotContain("\"SpO2\"", vitalsJson);
    }

    [Fact]
    public async Task CreatePatient_WhenRequiredFieldsMissing_ReturnsBadRequest()
    {
        var invalid = new PatientInfoDto("", "", 0, null, "Adu");
        var response = await _client.PostAsJsonAsync("/api/v1/patients", invalid);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task IngestHardwareAlert_ReturnsAccepted()
    {
        var alert = new AlertDto(
            AlertId: "P001:HeartRate",
            PatientId: "P001",
            VitalSign: VitalSignType.HeartRate,
            Severity: AlertSeverity.Critical,
            Message: "HR critically high: 162 bpm",
            Value: 162.0,
            Timestamp: DateTimeOffset.UtcNow,
            IsAcknowledged: false
        );

        var response = await _client.PostAsJsonAsync("/api/v1/ingestion/alerts", alert);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
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
