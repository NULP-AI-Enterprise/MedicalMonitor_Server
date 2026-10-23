using System.Net;
using System.Net.Http.Json;
using PatientMonitoring.Api.Contracts;
using PatientMonitoring.Api.Models;

namespace PatientMonitoring.IntegrationTests;

public class VitalSignsApiIntegrationTests : IClassFixture<PatientMonitoringWebApplicationFactory>
{
    private readonly PatientMonitoringWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public VitalSignsApiIntegrationTests(PatientMonitoringWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Record_ValidVitals_ReturnsCreatedWithEvaluatedStatus()
    {
        var patientId = await _factory.SeedPatientAsync("Андрій Бондар");

        var request = new RecordVitalSignsRequest
        {
            DeviceId = "ICU-BED-10",
            HeartRate = 72,
            SystolicBloodPressure = 120,
            DiastolicBloodPressure = 80,
            OxygenSaturation = 98.5,
            Temperature = 36.6,
            RespiratoryRate = 16
        };

        var response = await _client.PostAsJsonAsync($"/api/patients/{patientId}/vitals", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var vitals = await response.Content.ReadFromJsonAsync<VitalSignsResponse>();
        Assert.NotNull(vitals);
        Assert.Equal(patientId, vitals.PatientId);
        Assert.Equal(VitalStatus.Normal, vitals.Status);
        Assert.Empty(vitals.Alerts);
        Assert.Equal("Андрій Бондар", vitals.PatientFullName);

        // Verify latest endpoint
        var latestRes = await _client.GetAsync($"/api/patients/{patientId}/vitals/latest");
        Assert.Equal(HttpStatusCode.OK, latestRes.StatusCode);
        var latest = await latestRes.Content.ReadFromJsonAsync<VitalSignsResponse>();
        Assert.NotNull(latest);
        Assert.Equal(72, latest.HeartRate);
    }

    [Fact]
    public async Task Record_AbnormalVitals_ReturnsWarningOrCritical()
    {
        var patientId = await _factory.SeedPatientAsync("Василь Симоненко");

        var request = new RecordVitalSignsRequest
        {
            DeviceId = "ICU-BED-11",
            HeartRate = 150, // Critical High (> 130)
            SystolicBloodPressure = 190, // Critical High (> 180)
            DiastolicBloodPressure = 95,
            OxygenSaturation = 85.0 // Critical Low (< 88)
        };

        var response = await _client.PostAsJsonAsync($"/api/patients/{patientId}/vitals", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var vitals = await response.Content.ReadFromJsonAsync<VitalSignsResponse>();
        Assert.NotNull(vitals);
        Assert.Equal(VitalStatus.Critical, vitals.Status);
        Assert.NotEmpty(vitals.Alerts);
    }

    [Fact]
    public async Task RecordBatch_MultipleVitals_InsertsAllAndEvaluatesLatest()
    {
        var patientId = await _factory.SeedPatientAsync("Леся Українка");

        var batchRequest = new RecordVitalSignsBatchRequest
        {
            Items =
            [
                new RecordVitalSignsRequest
                {
                    RecordedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
                    HeartRate = 70,
                    OxygenSaturation = 98.0
                },
                new RecordVitalSignsRequest
                {
                    RecordedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                    HeartRate = 75,
                    OxygenSaturation = 97.5
                },
                new RecordVitalSignsRequest
                {
                    RecordedAt = DateTimeOffset.UtcNow,
                    HeartRate = 80,
                    OxygenSaturation = 99.0
                }
            ]
        };

        var response = await _client.PostAsJsonAsync($"/api/patients/{patientId}/vitals/batch", batchRequest);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<RecordVitalSignsBatchResponse>();
        Assert.NotNull(result);
        Assert.Equal(3, result.InsertedCount);
        Assert.NotNull(result.LatestRecorded);
        Assert.Equal(80, result.LatestRecorded.HeartRate);
    }

    [Fact]
    public async Task Export_VitalsCsv_ReturnsFileWithCsvContent()
    {
        var patientId = await _factory.SeedPatientAsync("Іван Франко");

        // Record a vital sign
        await _client.PostAsJsonAsync($"/api/patients/{patientId}/vitals", new RecordVitalSignsRequest
        {
            DeviceId = "DEV-EXPORT",
            HeartRate = 68,
            SystolicBloodPressure = 118,
            DiastolicBloodPressure = 78,
            OxygenSaturation = 98.0,
            Temperature = 36.5,
            RespiratoryRate = 14
        });

        // Request CSV export
        var response = await _client.GetAsync($"/api/patients/{patientId}/vitals/export?format=csv");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);

        var csvText = await response.Content.ReadAsStringAsync();
        Assert.Contains("PatientFullName", csvText);
        Assert.Contains("Іван Франко", csvText);
        Assert.Contains("DEV-EXPORT", csvText);
        Assert.Contains("68", csvText);
    }

    [Fact]
    public async Task Export_VitalsJson_ReturnsJsonArray()
    {
        var patientId = await _factory.SeedPatientAsync("Михайло Коцюбинський");

        // Record a vital sign
        await _client.PostAsJsonAsync($"/api/patients/{patientId}/vitals", new RecordVitalSignsRequest
        {
            DeviceId = "DEV-JSON",
            HeartRate = 74,
            OxygenSaturation = 99.0
        });

        // Request JSON export
        var response = await _client.GetAsync($"/api/patients/{patientId}/vitals/export?format=json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var list = await response.Content.ReadFromJsonAsync<List<VitalSignsResponse>>();
        Assert.NotNull(list);
        Assert.NotEmpty(list);
        Assert.Equal("Михайло Коцюбинський", list[0].PatientFullName);
    }

    [Fact]
    public async Task Ingest_ByWardAndBed_FindsPatientAndRecordsVitals()
    {
        var patientId = await _factory.SeedPatientAsync("Григорій Сковорода", ward: "Палата 7", bed: "3");

        var ingestReq = new IngestVitalSignsRequest
        {
            Ward = "Палата 7",
            Bed = "3",
            DeviceId = "PARSER-BLE-01",
            HeartRate = 77,
            OxygenSaturation = 98.2
        };

        var response = await _client.PostAsJsonAsync("/api/vitals/ingest", ingestReq);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var vitals = await response.Content.ReadFromJsonAsync<VitalSignsResponse>();
        Assert.NotNull(vitals);
        Assert.Equal(patientId, vitals.PatientId);
        Assert.Equal("Григорій Сковорода", vitals.PatientFullName);
        Assert.Equal(77, vitals.HeartRate);
    }

    [Fact]
    public async Task Ingest_WhenPatientNotExists_AutoProvisionsPatientAndRecordsVitals()
    {
        var ingestReq = new IngestVitalSignsRequest
        {
            Ward = "Хірургія",
            Bed = "10",
            PatientFullName = "Новий Пацієнт з Парсера",
            DeviceId = "PARSER-HL7-02",
            HeartRate = 84,
            Temperature = 36.8
        };

        var response = await _client.PostAsJsonAsync("/api/vitals/ingest", ingestReq);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var vitals = await response.Content.ReadFromJsonAsync<VitalSignsResponse>();
        Assert.NotNull(vitals);
        Assert.Equal("Новий Пацієнт з Парсера", vitals.PatientFullName);
        Assert.Equal(84, vitals.HeartRate);
    }
}
