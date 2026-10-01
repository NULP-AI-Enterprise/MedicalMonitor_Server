using System.Net;
using System.Net.Http.Json;
using PatientMonitoring.Api.Contracts;
using Xunit;

namespace PatientMonitoring.IntegrationTests;

public class UmecHl7IngestionIntegrationTests : IClassFixture<PatientMonitoringWebApplicationFactory>
{
    private readonly HttpClient _client;

    public UmecHl7IngestionIntegrationTests(PatientMonitoringWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task IngestUmecHl7_ValidPayload_ReturnsOkAndRecordsVitals()
    {
        var hl7 = "MSH|^~\\&|MINDRAY_UMEC|ICU|||20260925120000||ORU^R01|503|P|2.3.1\r" +
                  "PID|||P-999||Ivanenko^Oleg\r" +
                  "PV1||I|ICU^3\r" +
                  "OBX|1|NM|101^HR||82|bpm|||||F|||20260925120000\r" +
                  "OBX|2|NM|160^SpO2||97.0|%|||||F|||20260925120000\r" +
                  "OBX|3|NM|170^Sys||130|mmHg|||||F|||20260925120000\r" +
                  "OBX|4|NM|171^Dia||85|mmHg|||||F|||20260925120000\r" +
                  "OBX|5|NM|151^RR||18|rpm|||||F|||20260925120000\r" +
                  "OBX|6|NM|200^T1||36.8|C|||||F|||20260925120000\r";

        var payload = new IngestUmecHl7Request
        {
            RawMessage = hl7,
            DeviceId = "UMEC-BED-03"
        };

        var response = await _client.PostAsJsonAsync("/api/v1/ingestion/umec/hl7", payload);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<IngestUmecHl7Response>();
        Assert.NotNull(body);
        Assert.True(body.Success);
        Assert.Equal("Vitals", body.MessageKind);
        Assert.NotNull(body.Vitals);
        Assert.Equal(82, body.Vitals.HeartRate);
        Assert.Equal(97.0, body.Vitals.OxygenSaturation);
        Assert.Equal(130, body.Vitals.SystolicBloodPressure);
        Assert.Equal(85, body.Vitals.DiastolicBloodPressure);
        Assert.Equal(18, body.Vitals.RespiratoryRate);
        Assert.Equal(36.8, body.Vitals.Temperature);
        Assert.Contains("Ivanenko Oleg", body.PatientFullName);
    }
}
