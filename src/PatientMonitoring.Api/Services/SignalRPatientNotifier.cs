using Microsoft.AspNetCore.SignalR;
using PatientMonitoring.Api.Contracts;
using PatientMonitoring.Api.Hubs;

namespace PatientMonitoring.Api.Services;

public sealed class SignalRPatientNotifier(IHubContext<PatientMonitoringHub, IPatientMonitoringClient> hubContext)
    : IPatientNotifier
{
    public Task NotifyVitalSignsAsync(VitalSignsResponse vitals, CancellationToken cancellationToken = default)
    {
        var patientGroup = hubContext.Clients.Group(PatientMonitoringHub.PatientGroup(vitals.PatientId));
        var allGroup = hubContext.Clients.Group(PatientMonitoringHub.AllPatientsGroup);

        return Task.WhenAll(
            patientGroup.VitalSignsReceived(vitals),
            allGroup.VitalSignsReceived(vitals));
    }

    public Task NotifyAlertAsync(PatientAlertNotification alert, CancellationToken cancellationToken = default)
    {
        var patientGroup = hubContext.Clients.Group(PatientMonitoringHub.PatientGroup(alert.PatientId));
        var allGroup = hubContext.Clients.Group(PatientMonitoringHub.AllPatientsGroup);

        return Task.WhenAll(
            patientGroup.PatientAlert(alert),
            allGroup.PatientAlert(alert));
    }
}
