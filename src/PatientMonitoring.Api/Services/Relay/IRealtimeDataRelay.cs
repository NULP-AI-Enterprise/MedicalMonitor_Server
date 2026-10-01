using PatientMonitoring.Api.Contracts;

namespace PatientMonitoring.Api.Services.Relay;

public interface IRealtimeDataRelay
{
    Task RelayVitalSignsAsync(VitalSignsResponse vitals, CancellationToken cancellationToken = default);
    Task RelayBatchAsync(RecordVitalSignsBatchResponse batch, CancellationToken cancellationToken = default);
}
