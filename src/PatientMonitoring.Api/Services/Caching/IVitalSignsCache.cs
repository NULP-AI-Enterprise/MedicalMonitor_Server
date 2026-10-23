using PatientMonitoring.Api.Contracts;

namespace PatientMonitoring.Api.Services.Caching;

public interface IVitalSignsCache
{
    Task<VitalSignsResponse?> GetLatestAsync(Guid patientId, CancellationToken cancellationToken = default);

    Task SetLatestAsync(Guid patientId, VitalSignsResponse vitals, CancellationToken cancellationToken = default);

    Task InvalidateAsync(Guid patientId, CancellationToken cancellationToken = default);
}
