using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Monitoring.Shared;
using Monitoring.Shared.DTOs;

namespace PatientMonitoring.IntegrationTests;

public class CentralStationFixesIntegrationTests : IClassFixture<PatientMonitoringWebApplicationFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly PatientMonitoringWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public CentralStationFixesIntegrationTests(PatientMonitoringWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task CreatePatient_WithExistingId_ReturnsConflict()
    {
        var patient = new PatientInfoDto($"DUP_{Guid.NewGuid():N}", "Duplicate Test", 70, null, "Adu");

        var first = await _client.PostAsJsonAsync("/api/v1/patients", patient);
        var second = await _client.PostAsJsonAsync("/api/v1/patients", patient);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task UpdatePatient_IntoOccupiedBed_ReturnsConflict()
    {
        var first = $"BEDA_{Guid.NewGuid():N}";
        var second = $"BEDB_{Guid.NewGuid():N}";
        await _client.PostAsJsonAsync("/api/v1/patients", new PatientInfoDto(first, "Bed A", 80, null, "Adu"));
        await _client.PostAsJsonAsync("/api/v1/patients", new PatientInfoDto(second, "Bed B", 81, null, "Adu"));

        var response = await _client.PutAsJsonAsync($"/api/v1/patients/{first}", new PatientInfoDto(first, "Bed A", 81, null, "Adu"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task CreatePatient_WithUnknownPatientType_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/patients",
            new PatientInfoDto($"TYPE_{Guid.NewGuid():N}", "Type Test", 0, null, "Unknown"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task NewPatient_HasNoVitalsUntilADeviceReportsThem()
    {
        var patientId = $"NOV_{Guid.NewGuid():N}";
        await _client.PostAsJsonAsync("/api/v1/patients", new PatientInfoDto(patientId, "No Vitals", 71, null, "Adu"));

        var response = await _client.GetAsync($"/api/v1/patients/{patientId}/vitals/latest");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task IngestTelemetry_WithoutNibp_ReturnsBadRequest()
    {
        var body = new
        {
            patientId = "P002",
            patientName = "Doe, Jane",
            bedNumber = 2,
            vitals = new { heartRate = 80, spO2 = 97, nibp = (object?)null, respiratoryRate = 16, temperature = 36.6, pulseRate = 80 },
            waveforms = Array.Empty<object>(),
            timestamp = DateTimeOffset.UtcNow
        };

        var response = await _client.PostAsJsonAsync("/api/v1/ingestion/telemetry", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task TemperatureAlertMessage_UsesDecimalPoint()
    {
        var patientId = $"TMP_{Guid.NewGuid():N}";
        var telemetry = new MonitorUpdateDto(
            patientId,
            "Temperature Test",
            0,
            new VitalsDto(80, 97, new BloodPressureDto(120, 80, 93), 16, 35.5, 80),
            Array.Empty<WaveformSampleDto>(),
            DateTimeOffset.UtcNow);

        var ingest = await _client.PostAsJsonAsync("/api/v1/ingestion/telemetry", telemetry);
        Assert.Equal(HttpStatusCode.Accepted, ingest.StatusCode);

        var alerts = await _client.GetFromJsonAsync<List<AlertDto>>("/api/v1/alerts/active", JsonOptions);
        var temperature = Assert.Single(alerts!, a => a.PatientId == patientId && a.VitalSign == VitalSignType.Temperature);
        Assert.Equal("TEMP low: 35.5 °C", temperature.Message);
    }

    [Fact]
    public async Task HardwareAlarm_IsListed_AndAcknowledgingItClearsIt()
    {
        var patientId = $"HW_{Guid.NewGuid():N}";
        await _client.PostAsJsonAsync("/api/v1/patients", new PatientInfoDto(patientId, "Hardware Test", 72, null, "Adu"));
        var alarm = new AlertDto("ignored", patientId, VitalSignType.SpO2, AlertSeverity.Critical, "SpO2 critically low: 80 %", 80, DateTimeOffset.UtcNow, false);

        var ingest = await _client.PostAsJsonAsync("/api/v1/ingestion/alerts", alarm);
        Assert.Equal(HttpStatusCode.Accepted, ingest.StatusCode);

        var active = await _client.GetFromJsonAsync<List<AlertDto>>("/api/v1/alerts/active", JsonOptions);
        var stored = Assert.Single(active!, a => a.PatientId == patientId);
        Assert.Equal($"{patientId}:SpO2", stored.AlertId);

        var ack = await _client.PostAsync($"/api/v1/alerts/{stored.AlertId}/acknowledge", null);
        Assert.Equal(HttpStatusCode.OK, ack.StatusCode);

        var afterAck = await _client.GetFromJsonAsync<List<AlertDto>>("/api/v1/alerts/active", JsonOptions);
        Assert.DoesNotContain(afterAck!, a => a.PatientId == patientId);
    }

    [Fact]
    public async Task HardwareAlarm_StaysUntilAcknowledged_EvenWhenVitalsAreNormal()
    {
        var patientId = $"LATCH_{Guid.NewGuid():N}";
        await PostVitalsAsync(patientId, heartRate: 75);
        var alarm = new AlertDto("ignored", patientId, VitalSignType.SpO2, AlertSeverity.Critical, "SpO2 critically low: 80 %", 80, DateTimeOffset.UtcNow, false);
        var ingest = await _client.PostAsJsonAsync("/api/v1/ingestion/alerts", alarm);
        Assert.Equal(HttpStatusCode.Accepted, ingest.StatusCode);

        await Task.Delay(1200);   // two ticks with normal SpO2 must not clear a device alarm
        var before = await _client.GetFromJsonAsync<List<AlertDto>>("/api/v1/alerts/active", JsonOptions);
        Assert.Contains(before!, a => a.PatientId == patientId && a.VitalSign == VitalSignType.SpO2);

        var ack = await _client.PostAsync($"/api/v1/alerts/{patientId}:SpO2/acknowledge", null);
        Assert.Equal(HttpStatusCode.OK, ack.StatusCode);

        var after = await _client.GetFromJsonAsync<List<AlertDto>>("/api/v1/alerts/active", JsonOptions);
        Assert.DoesNotContain(after!, a => a.PatientId == patientId && a.VitalSign == VitalSignType.SpO2);
    }

    [Fact]
    public async Task HardwareAlarm_ForUnknownPatient_ReturnsNotFound()
    {
        var alarm = new AlertDto(
            "ignored",
            $"UNKNOWN_{Guid.NewGuid():N}",
            VitalSignType.HeartRate,
            AlertSeverity.Warning,
            "HR high: 120 bpm",
            120,
            DateTimeOffset.UtcNow,
            false);

        var response = await _client.PostAsJsonAsync("/api/v1/ingestion/alerts", alarm);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task BreachThatChangesDirection_IsSilentAsInTheReference()
    {
        var patientId = $"DIR_{Guid.NewGuid():N}";
        var received = new List<AlertDto>();
        var connection = new Microsoft.AspNetCore.SignalR.Client.HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, HubMethods.Route), options =>
            {
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
            })
            .AddJsonProtocol(options =>
            {
                options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter());
            })
            .Build();
        connection.On<AlertDto>(HubMethods.AlertTriggered, alert =>
        {
            lock (received) received.Add(alert);
        });

        await connection.StartAsync();
        try
        {
            await PostVitalsAsync(patientId, heartRate: 48);    // low (Warning)
            await PostVitalsAsync(patientId, heartRate: 115);   // high (Warning): same severity, new direction

            // As in the reference: a change of direction at the same severity is a silent refresh.
            await Task.Delay(1000);
            lock (received) Assert.Equal(1, received.Count(a => a.PatientId == patientId));
            var active = await _client.GetFromJsonAsync<List<AlertDto>>("/api/v1/alerts/active", JsonOptions);
            Assert.Contains(active!, a => a.PatientId == patientId && a.Message == "HR high: 115 bpm");
        }
        finally
        {
            await connection.StopAsync();
        }
    }

    [Fact]
    public async Task Timestamps_AreWrittenInContractFormat()
    {
        var json = await _client.GetStringAsync("/api/v1/health");

        var match = Regex.Match(json, "\"timestamp\":\"([^\"]+)\"");
        Assert.True(match.Success);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$", match.Groups[1].Value);
    }

    [Fact]
    public async Task CreatePatient_WithBedZero_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/patients",
            new PatientInfoDto($"BED0_{Guid.NewGuid():N}", "Bed Zero", 0, null, "Adu"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreatePatient_IntoOccupiedBed_ReturnsConflict()
    {
        var first = await _client.PostAsJsonAsync("/api/v1/patients",
            new PatientInfoDto($"OCC1_{Guid.NewGuid():N}", "Occupied First", 73, null, "Adu"));
        var second = await _client.PostAsJsonAsync("/api/v1/patients",
            new PatientInfoDto($"OCC2_{Guid.NewGuid():N}", "Occupied Second", 73, null, "Adu"));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Telemetry_MeanPressureIsRecomputedAndTemperatureRounded()
    {
        var patientId = $"NORM_{Guid.NewGuid():N}";
        var telemetry = new MonitorUpdateDto(
            patientId,
            "Normalize Test",
            0,
            new VitalsDto(80, 97, new BloodPressureDto(120, 80, 999), 16, 36.55, 80),
            Array.Empty<WaveformSampleDto>(),
            DateTimeOffset.UtcNow);

        var ingest = await _client.PostAsJsonAsync("/api/v1/ingestion/telemetry", telemetry);
        Assert.Equal(HttpStatusCode.Accepted, ingest.StatusCode);

        var latest = await _client.GetFromJsonAsync<VitalsDto>($"/api/v1/patients/{patientId}/vitals/latest");
        Assert.NotNull(latest);
        Assert.Equal(93, latest.Nibp.MeanArterialPressure);
        Assert.Equal(Math.Round(36.55, 1), latest.Temperature);
    }

    [Fact]
    public async Task Cors_AllowsOnlyHttpLocalhostOrigins()
    {
        var localhost = new HttpRequestMessage(HttpMethod.Get, "/api/v1/health");
        localhost.Headers.Add("Origin", "http://localhost:5173");
        var localhostResponse = await _client.SendAsync(localhost);

        var loopback = new HttpRequestMessage(HttpMethod.Get, "/api/v1/health");
        loopback.Headers.Add("Origin", "http://127.0.0.1:5173");
        var loopbackResponse = await _client.SendAsync(loopback);

        Assert.Equal("http://localhost:5173", localhostResponse.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.False(loopbackResponse.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task BreachThatChangesDirectionAfterAcknowledgement_StaysSilencedAsInTheReference()
    {
        var patientId = $"DIRACK_{Guid.NewGuid():N}";
        var triggered = new List<AlertDto>();
        var acknowledged = new List<AlertDto>();

        var connection = new Microsoft.AspNetCore.SignalR.Client.HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, HubMethods.Route), options =>
            {
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
            })
            .AddJsonProtocol(options =>
            {
                options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter());
            })
            .Build();
        connection.On<AlertDto>(HubMethods.AlertTriggered, alert => { lock (triggered) triggered.Add(alert); });
        connection.On<AlertDto>(HubMethods.AlertAcknowledged, alert => { lock (acknowledged) acknowledged.Add(alert); });

        await connection.StartAsync();
        try
        {
            await PostVitalsAsync(patientId, heartRate: 48);      // low (Warning)
            await connection.SendAsync("AcknowledgeAlert", $"{patientId}:HeartRate");
            Assert.NotNull(await WaitForAsync(() => { lock (acknowledged) return acknowledged.FirstOrDefault(a => a.PatientId == patientId); }, 3000));

            int announced;
            lock (triggered) announced = triggered.Count(a => a.PatientId == patientId);
            await PostVitalsAsync(patientId, heartRate: 115);     // high (Warning): same severity, new direction

            // As in the reference: a silent refresh. The alert stays acknowledged and its text is updated in the alert state.
            await Task.Delay(1000);
            lock (triggered) Assert.Equal(announced, triggered.Count(a => a.PatientId == patientId));
            var active = await _client.GetFromJsonAsync<List<AlertDto>>("/api/v1/alerts/active", JsonOptions);
            var stored = Assert.Single(active!, a => a.PatientId == patientId && a.VitalSign == VitalSignType.HeartRate);
            Assert.True(stored.IsAcknowledged);
            Assert.Equal("HR high: 115 bpm", stored.Message);
        }
        finally
        {
            await connection.StopAsync();
        }
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

    private async Task PostVitalsAsync(string patientId, int heartRate)
    {
        var telemetry = new MonitorUpdateDto(
            patientId,
            "Vitals Test",
            0,
            new VitalsDto(heartRate, 97, new BloodPressureDto(120, 80, 93), 16, 36.6, heartRate),
            Array.Empty<WaveformSampleDto>(),
            DateTimeOffset.UtcNow);

        var response = await _client.PostAsJsonAsync("/api/v1/ingestion/telemetry", telemetry);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }
}
