using Monitoring.Shared.DTOs;
using PatientMonitoring.Api.Models;

namespace PatientMonitoring.Api.Services.CentralStation;

public interface ICentralStationService
{
    int ConnectedStationsCount { get; }
    void IncrementConnectedStations();
    void DecrementConnectedStations();

    Task<IReadOnlyList<PatientInfoDto>> GetPatientsAsync(CancellationToken ct = default);
    Task<PatientInfoDto?> GetPatientAsync(string patientId, CancellationToken ct = default);
    Task<PatientInfoDto> CreatePatientAsync(PatientInfoDto dto, CancellationToken ct = default);
    Task<PatientInfoDto?> UpdatePatientAsync(string patientId, PatientInfoDto dto, CancellationToken ct = default);
    Task<bool> DeletePatientAsync(string patientId, CancellationToken ct = default);

    VitalsDto? GetLatestVitals(string patientId);
    IReadOnlyList<AlertDto> GetActiveAlerts();
    Task<AlertDto?> AcknowledgeAlertAsync(string alertId);

    Task ProcessTelemetryAsync(MonitorUpdateDto telemetry, CancellationToken ct = default);
    Task ProcessAlertAsync(AlertDto alert, CancellationToken ct = default);
    Task ProcessVitalSignsRecordedAsync(Patient patient, VitalSign vitals, CancellationToken ct = default);
    Task PublishTickAsync(CancellationToken ct = default);
}
