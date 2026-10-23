using PatientMonitoring.Api.Contracts;

namespace PatientMonitoring.Api.Services;

/// <summary>Абстракція над транспортом real-time сповіщень (зараз — SignalR).</summary>
public interface IPatientNotifier
{
    Task NotifyVitalSignsAsync(VitalSignsResponse vitals, CancellationToken cancellationToken = default);

    Task NotifyAlertAsync(PatientAlertNotification alert, CancellationToken cancellationToken = default);
}
