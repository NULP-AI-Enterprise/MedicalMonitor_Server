using FastEndpoints;
using Moq;
using PatientMonitoring.Api.Contracts;
using PatientMonitoring.Api.Endpoints.Patients;
using PatientMonitoring.Api.Endpoints.VitalSigns;
using PatientMonitoring.Api.Models;
using PatientMonitoring.Api.Services;

namespace PatientMonitoring.UnitTests;

public class FastEndpointsTests
{
    [Fact]
    public async Task GetPatientByIdEndpoint_WhenPatientExists_ReturnsOk()
    {
        var patientId = Guid.NewGuid();
        var patient = new PatientResponse(patientId, "Іван Іванов", null, "1", "A", true, DateTime.UtcNow, null);
        var mockService = new Mock<IPatientService>();
        mockService.Setup(s => s.GetByIdAsync(patientId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(patient);

        var endpoint = Factory.Create<GetPatientByIdEndpoint>(mockService.Object);
        endpoint.HttpContext.Request.RouteValues["id"] = patientId.ToString();

        await endpoint.HandleAsync(CancellationToken.None);

        Assert.Equal(200, endpoint.HttpContext.Response.StatusCode);
        Assert.Equal(patientId, endpoint.Response.Id);
    }

    [Fact]
    public async Task GetPatientByIdEndpoint_WhenPatientNotFound_Returns404()
    {
        var mockService = new Mock<IPatientService>();
        mockService.Setup(s => s.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PatientResponse?)null);

        var endpoint = Factory.Create<GetPatientByIdEndpoint>(mockService.Object);
        endpoint.HttpContext.Request.RouteValues["id"] = Guid.NewGuid().ToString();

        await endpoint.HandleAsync(CancellationToken.None);

        Assert.Equal(404, endpoint.HttpContext.Response.StatusCode);
    }

    [Fact]
    public async Task GetLatestPatientVitalsEndpoint_WhenNotFound_Returns404()
    {
        var mockService = new Mock<IVitalSignsService>();
        mockService.Setup(s => s.GetLatestAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((VitalSignsResponse?)null);

        var endpoint = Factory.Create<GetLatestPatientVitalsEndpoint>(mockService.Object);
        endpoint.HttpContext.Request.RouteValues["patientId"] = Guid.NewGuid().ToString();

        await endpoint.HandleAsync(CancellationToken.None);

        Assert.Equal(404, endpoint.HttpContext.Response.StatusCode);
    }

    [Fact]
    public async Task RecordVitalsEndpoint_WhenPatientNotFound_ReturnsNotFound()
    {
        var mockService = new Mock<IVitalSignsService>();
        mockService.Setup(s => s.RecordAsync(It.IsAny<Guid>(), It.IsAny<RecordVitalSignsRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((VitalSignsResponse?)null);

        var endpoint = Factory.Create<RecordVitalsEndpoint>(mockService.Object);
        endpoint.HttpContext.Request.RouteValues["patientId"] = Guid.NewGuid().ToString();

        await endpoint.HandleAsync(new RecordVitalSignsRequest { HeartRate = 80 }, CancellationToken.None);

        Assert.Equal(404, endpoint.HttpContext.Response.StatusCode);
    }

    [Fact]
    public async Task ExportPatientVitalsEndpoint_Csv_ReturnsFile()
    {
        var patientId = Guid.NewGuid();
        var mockService = new Mock<IVitalSignsService>();
        var sampleVitals = new VitalSignsResponse(
            1, patientId, DateTime.UtcNow, DateTime.UtcNow, "DEV-1", 72, 120, 80, 99.0, 36.6, 16,
            VitalStatus.Normal, [], "Петро Петренко");

        mockService.Setup(s => s.GetHistoryAsync(patientId, null, null, 1000, It.IsAny<CancellationToken>()))
            .ReturnsAsync([sampleVitals]);

        var endpoint = Factory.Create<ExportPatientVitalsEndpoint>(mockService.Object);
        endpoint.HttpContext.Request.RouteValues["patientId"] = patientId.ToString();
        endpoint.HttpContext.Request.QueryString = new Microsoft.AspNetCore.Http.QueryString("?format=csv");

        await endpoint.HandleAsync(CancellationToken.None);

        Assert.Equal(200, endpoint.HttpContext.Response.StatusCode);
        Assert.Equal("text/csv", endpoint.HttpContext.Response.ContentType);
    }

    [Fact]
    public async Task ExportPatientVitalsEndpoint_Json_ReturnsOk()
    {
        var patientId = Guid.NewGuid();
        var mockService = new Mock<IVitalSignsService>();
        var sampleVitals = new VitalSignsResponse(
            1, patientId, DateTime.UtcNow, DateTime.UtcNow, "DEV-1", 72, 120, 80, 99.0, 36.6, 16,
            VitalStatus.Normal, [], "Петро Петренко");

        mockService.Setup(s => s.GetHistoryAsync(patientId, null, null, 1000, It.IsAny<CancellationToken>()))
            .ReturnsAsync([sampleVitals]);

        var endpoint = Factory.Create<ExportPatientVitalsEndpoint>(mockService.Object);
        endpoint.HttpContext.Request.RouteValues["patientId"] = patientId.ToString();
        endpoint.HttpContext.Request.QueryString = new Microsoft.AspNetCore.Http.QueryString("?format=json");

        await endpoint.HandleAsync(CancellationToken.None);

        Assert.Equal(200, endpoint.HttpContext.Response.StatusCode);
    }
}
