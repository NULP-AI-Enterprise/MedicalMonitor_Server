using MedicalMonitorServer.DeviceDriver.Umec;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PatientMonitoring.Api.Contracts;
using PatientMonitoring.Api.Data;
using PatientMonitoring.Api.Models;
using PatientMonitoring.Api.Services;
using PatientMonitoring.Api.Services.Parser;
using Xunit;

namespace PatientMonitoring.UnitTests;

public sealed class UmecParserBridgeTests
{
    private readonly ParserService _parserService = new();

    private static AppDbContext CreateInMemoryDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    [Fact]
    public async Task ParseRawHl7_ValidUmecMessage_ParsesVitalsAndPatientSuccessfully()
    {
        // Arrange
        using var db = CreateInMemoryDbContext();
        var mockVitalsService = new Mock<IVitalSignsService>();

        mockVitalsService
            .Setup(s => s.RecordAsync(It.IsAny<Guid>(), It.IsAny<RecordVitalSignsRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid pid, RecordVitalSignsRequest req, CancellationToken _) =>
                new VitalSignsResponse(
                    Id: 1,
                    PatientId: pid,
                    RecordedAt: DateTime.UtcNow,
                    ReceivedAt: DateTime.UtcNow,
                    DeviceId: req.DeviceId,
                    HeartRate: req.HeartRate,
                    SystolicBloodPressure: req.SystolicBloodPressure,
                    DiastolicBloodPressure: req.DiastolicBloodPressure,
                    OxygenSaturation: req.OxygenSaturation,
                    Temperature: req.Temperature,
                    RespiratoryRate: req.RespiratoryRate,
                    Status: VitalStatus.Normal,
                    Alerts: []));

        var bridge = new UmecParserBridge(_parserService, db, mockVitalsService.Object, NullLogger<UmecParserBridge>.Instance);

        var hl7Raw = "MSH|^~\\&|MINDRAY_UMEC|ICU|||20260925120000||ORU^R01|503|P|2.3.1\r" +
                     "PID|||P-402||Shevchenko^Taras\r" +
                     "PV1||I|^^Cardiology&1\r" +
                     "OBX|1|NM|101^HR||74|bpm|||||F|||20260925120000\r" +
                     "OBX|2|NM|160^SpO2||98.5|%|||||F|||20260925120000\r" +
                     "OBX|3|NM|170^Sys||122|mmHg|||||F|||20260925120000\r" +
                     "OBX|4|NM|171^Dia||78|mmHg|||||F|||20260925120000\r" +
                     "OBX|5|NM|151^RR||16|rpm|||||F|||20260925120000\r" +
                     "OBX|6|NM|200^T1||36.6|C|||||F|||20260925120000\r";

        // Act
        var result = await bridge.IngestRawHl7Async(hl7Raw, defaultDeviceId: "UMEC-ICU-01");

        // Assert
        Assert.NotNull(result);
        Assert.True(result.Success);
        Assert.NotNull(result.Vitals);
        Assert.Equal(74, result.Vitals.HeartRate);
        Assert.Equal(98.5, result.Vitals.OxygenSaturation);
        Assert.Equal(122, result.Vitals.SystolicBloodPressure);
        Assert.Equal(78, result.Vitals.DiastolicBloodPressure);
        Assert.Equal(16, result.Vitals.RespiratoryRate);
        Assert.Equal(36.6, result.Vitals.Temperature);

        // Verify patient auto-provisioning
        var savedPatient = await db.Patients.FirstOrDefaultAsync(p => p.Id == result.PatientId);
        Assert.NotNull(savedPatient);
        Assert.Equal("Shevchenko Taras", savedPatient.FullName);
        Assert.Equal("Cardiology", savedPatient.Ward);
        Assert.Equal("1", savedPatient.Bed);
    }

    [Fact]
    public async Task ParseRawHl7_InvalidMessage_ReturnsFailure()
    {
        using var db = CreateInMemoryDbContext();
        var mockVitalsService = new Mock<IVitalSignsService>();
        var bridge = new UmecParserBridge(_parserService, db, mockVitalsService.Object, NullLogger<UmecParserBridge>.Instance);

        var result = await bridge.IngestRawHl7Async("GARBAGE DATA THAT IS NOT HL7");

        Assert.False(result.Success);
        Assert.Null(result.Vitals);
    }
}
