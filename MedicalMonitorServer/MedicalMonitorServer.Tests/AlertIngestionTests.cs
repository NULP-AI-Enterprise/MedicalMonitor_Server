using FastEndpoints;
using MedicalMonitorServer.Contracts.Api;
using MedicalMonitorServer.Core.Monitoring;
using MedicalMonitorServer.Endpoints;
using MedicalMonitorServer.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace MedicalMonitorServer.Tests;

/// <summary>
/// Alerts posted by a gateway (<c>POST /api/v1/ingestion/alerts</c>). An alert
/// for a patient the server does not know must be turned away: there is no bed
/// to show it on, and it would sit in the active list with nobody able to
/// place it.
/// </summary>
public class AlertIngestionTests
{
    private readonly FakeHubContext _hub = new();
    private readonly PatientRegistry _patients = new();
    private readonly AlertStateService _alerts = new();
    private readonly MonitorBindings _bindings = new();

    private MonitorPipeline Pipeline() => new(
        _hub, _patients, _alerts, _bindings, new FeedStatus(), NullLogger<MonitorPipeline>.Instance);

    private void Admit(string patientId) =>
        _patients.Upsert(new PatientInfoDto(patientId, "Doe Jane", 3, null));

    private static AlertDto DeviceAlert(string patientId, int code = 10002) => new(
        AlertDto.BuildDeviceId(patientId, code),
        patientId,
        VitalSign: null,
        AlertSeverity.Critical,
        "HR Too Low",
        Value: null,
        DateTimeOffset.UtcNow,
        Source: AlertSource.Device,
        Code: code,
        Level: DeviceAlarmLevel.High,
        Category: AlertCategory.Physiological);

    [Fact]
    public async Task An_alert_for_an_unknown_patient_is_not_stored_or_broadcast()
    {
        await Pipeline().IngestAlertAsync(DeviceAlert("NOBODY"));

        Assert.Empty(_alerts.GetActiveAlerts());
        Assert.Empty(_hub.Sent);
    }

    [Fact]
    public async Task An_alert_for_a_known_patient_is_stored_and_broadcast()
    {
        Admit("P-1");

        await Pipeline().IngestAlertAsync(DeviceAlert("P-1"));

        var active = Assert.Single(_alerts.GetActiveAlerts());
        Assert.Equal("P-1", active.PatientId);
        var broadcast = Assert.Single(_hub.SentTo<AlertDto>(HubMethods.AlertTriggered));
        Assert.Equal(active.AlertId, broadcast.AlertId);
    }

    // ------------------------------------------------------------ the endpoint

    private IngestAlertEndpoint Endpoint() =>
        Factory.Create<IngestAlertEndpoint>(Pipeline(), _patients);

    [Fact]
    public async Task The_endpoint_answers_404_for_a_patient_it_does_not_know()
    {
        var endpoint = Endpoint();

        await endpoint.HandleAsync(DeviceAlert("NOBODY"), default);

        Assert.Equal(StatusCodes.Status404NotFound, endpoint.HttpContext.Response.StatusCode);
        Assert.Empty(_alerts.GetActiveAlerts());
        Assert.Empty(_hub.Sent);
    }

    [Fact]
    public async Task The_endpoint_accepts_an_alert_for_a_patient_on_the_roster()
    {
        Admit("P-1");
        var endpoint = Endpoint();

        await endpoint.HandleAsync(DeviceAlert("P-1"), default);

        Assert.Equal(StatusCodes.Status202Accepted, endpoint.HttpContext.Response.StatusCode);
        Assert.Single(_alerts.GetActiveAlerts());
    }

    [Fact]
    public async Task The_endpoint_still_answers_400_when_the_patient_id_is_missing()
    {
        // The existing validation must keep its status: 404 is only for a
        // well-formed alert about somebody who is not there.
        var endpoint = Endpoint();

        await endpoint.HandleAsync(DeviceAlert(" "), default);

        Assert.Equal(StatusCodes.Status400BadRequest, endpoint.HttpContext.Response.StatusCode);
        Assert.Empty(_alerts.GetActiveAlerts());
    }

    [Fact]
    public async Task An_alert_for_a_patient_who_was_removed_is_turned_away_too()
    {
        Admit("P-1");
        var pipeline = Pipeline();
        await pipeline.RemovePatientAsync("P-1");
        int sentBefore = _hub.Sent.Count;

        await pipeline.IngestAlertAsync(DeviceAlert("P-1"));

        Assert.Empty(_alerts.GetActiveAlerts());
        Assert.Equal(sentBefore, _hub.Sent.Count);
    }
}
