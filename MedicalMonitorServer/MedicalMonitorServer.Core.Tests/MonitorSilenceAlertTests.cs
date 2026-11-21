using MedicalMonitorServer.Contracts.Api;
using MedicalMonitorServer.Core.Monitoring;

namespace MedicalMonitorServer.Core.Tests;

/// <summary>
/// The "monitor is silent" alert inside the alert store: which of the store's
/// reconcile passes may remove it, and which must leave it alone.
/// </summary>
public class MonitorSilenceAlertTests
{
    private const string Patient = "P-1";

    private static AlertDto Silence(string patientId = Patient) => new(
        AlertDto.BuildMonitorSilentId(patientId), patientId, null,
        AlertDto.SeverityOf(DeviceAlarmLevel.High), "Monitor 10.0.0.5 is not sending data", null,
        DateTimeOffset.UtcNow, Source: AlertSource.Device, Code: AlertDto.MonitorSilentCode,
        Level: DeviceAlarmLevel.High, Category: AlertCategory.Technical);

    private static AlertDto Device(int code, AlertCategory category) => new(
        AlertDto.BuildDeviceId(Patient, code), Patient, null, AlertSeverity.Warning, "alarm", null,
        DateTimeOffset.UtcNow, Source: AlertSource.Device, Code: code, Level: DeviceAlarmLevel.Medium,
        Category: category);

    [Fact]
    public void SilenceId_IsOutsideTheDeviceCodeNamespace()
    {
        // A gateway may post any code it likes, including the reserved one.
        Assert.NotEqual(
            AlertDto.BuildDeviceId(Patient, AlertDto.MonitorSilentCode),
            AlertDto.BuildMonitorSilentId(Patient));
        Assert.True(AlertDto.MonitorSilentCode < 0);
    }

    [Fact]
    public void Clear_RemovesOnlyThatAlert_AndReturnsIt()
    {
        var svc = new AlertStateService();
        svc.Raise(Silence());
        svc.Raise(Device(10002, AlertCategory.Physiological));

        var cleared = svc.Clear(AlertDto.BuildMonitorSilentId(Patient));

        Assert.Equal(AlertDto.BuildMonitorSilentId(Patient), cleared?.AlertId);
        Assert.Equal(AlertDto.BuildDeviceId(Patient, 10002), Assert.Single(svc.GetActiveAlerts()).AlertId);
    }

    [Theory]
    [InlineData("P-1:monitor-silent")]
    [InlineData("")]
    [InlineData("   ")]
    public void Clear_NothingToRemove_ReturnsNull(string alertId)
    {
        var svc = new AlertStateService();

        Assert.Null(svc.Clear(alertId));
    }

    [Fact]
    public void Raise_WhileAlreadyUp_ReturnsNullAndKeepsTheAcknowledgement()
    {
        var svc = new AlertStateService();
        Assert.NotNull(svc.Raise(Silence()));
        svc.Acknowledge(AlertDto.BuildMonitorSilentId(Patient));

        Assert.Null(svc.Raise(Silence()));

        Assert.True(Assert.Single(svc.GetActiveAlerts()).IsAcknowledged);
    }

    [Fact]
    public void ThresholdReconcile_LeavesTheSilenceAlert()
    {
        var svc = new AlertStateService();
        svc.Raise(Silence());

        var changes = svc.Reconcile(Patient, new VitalReadings(HeartRate: 70));

        Assert.Empty(changes.Cleared);
        Assert.Single(svc.GetActiveAlerts());
    }

    [Fact]
    public void PhysiologicalDeviceReconcile_LeavesTheSilenceAlert()
    {
        var svc = new AlertStateService();
        svc.Raise(Silence());

        var changes = svc.ReconcileDevice(Patient, AlertCategory.Physiological, Array.Empty<AlertDto>());

        Assert.Empty(changes.Cleared);
        Assert.Single(svc.GetActiveAlerts());
    }

    [Fact]
    public void TheMonitorsOwnTechnicalList_SupersedesTheSilenceAlert()
    {
        // The alert is shaped as a technical device alarm, so the technical list a
        // returning monitor sends replaces it — which is right: it is talking again.
        var svc = new AlertStateService();
        svc.Raise(Silence());

        var changes = svc.ReconcileDevice(Patient, AlertCategory.Technical, Array.Empty<AlertDto>());

        Assert.Equal(AlertDto.BuildMonitorSilentId(Patient), Assert.Single(changes.Cleared).AlertId);
        Assert.Empty(svc.GetActiveAlerts());
    }

    [Fact]
    public void ClearForPatient_TakesTheSilenceAlertWithThePatient()
    {
        var svc = new AlertStateService();
        svc.Raise(Silence());
        svc.Raise(Silence("P-2"));

        svc.ClearForPatient(Patient);

        Assert.Equal("P-2", Assert.Single(svc.GetActiveAlerts()).PatientId);
    }
}
