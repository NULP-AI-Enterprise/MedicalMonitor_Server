using PatientMonitoring.Api.Contracts;

namespace PatientMonitoring.Api.Hubs;

/// <summary>
/// Методи, які сервер викликає на боці клієнта. Назви методів — це імена подій,
/// на які підписується клієнт (connection.on("VitalSignsReceived", ...)).
/// </summary>
public interface IPatientMonitoringClient
{
    /// <summary>Нові показники пацієнта (надсилається при кожному вимірюванні).</summary>
    Task VitalSignsReceived(VitalSignsResponse vitals);

    /// <summary>Стан пацієнта вийшов за межі норми (Warning або Critical).</summary>
    Task PatientAlert(PatientAlertNotification alert);
}
