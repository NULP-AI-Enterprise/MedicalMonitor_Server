using PatientMonitoring.Api.Contracts;

namespace PatientMonitoring.Api.Services.Alerting;

public interface IAlertChannel
{
    string Name { get; }

    bool IsEnabled { get; }

    Task SendAlertAsync(PatientAlertNotification alert, CancellationToken cancellationToken = default);
}

public interface IAlertDispatcher
{
    Task DispatchAsync(PatientAlertNotification alert, CancellationToken cancellationToken = default);
}
