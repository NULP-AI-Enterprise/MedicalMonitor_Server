using Microsoft.Extensions.Caching.Memory;
using PatientMonitoring.Api.Contracts;

namespace PatientMonitoring.Api.Services.Caching;

public sealed class MemoryVitalSignsCache(IMemoryCache cache, ILogger<MemoryVitalSignsCache> logger) : IVitalSignsCache
{
    private static readonly TimeSpan DefaultExpiration = TimeSpan.FromMinutes(10);

    private static string CacheKey(Guid patientId) => $"vitals:latest:{patientId}";

    public Task<VitalSignsResponse?> GetLatestAsync(Guid patientId, CancellationToken cancellationToken = default)
    {
        if (cache.TryGetValue(CacheKey(patientId), out VitalSignsResponse? vitals) && vitals is not null)
        {
            logger.LogDebug("Cache hit for patient {PatientId}", patientId);
            return Task.FromResult<VitalSignsResponse?>(vitals);
        }

        logger.LogDebug("Cache miss for patient {PatientId}", patientId);
        return Task.FromResult<VitalSignsResponse?>(null);
    }

    public Task SetLatestAsync(Guid patientId, VitalSignsResponse vitals, CancellationToken cancellationToken = default)
    {
        cache.Set(CacheKey(patientId), vitals, new MemoryCacheEntryOptions
        {
            SlidingExpiration = DefaultExpiration,
            Size = 1
        });

        logger.LogDebug("Cache updated for patient {PatientId}", patientId);
        return Task.CompletedTask;
    }

    public Task InvalidateAsync(Guid patientId, CancellationToken cancellationToken = default)
    {
        cache.Remove(CacheKey(patientId));
        logger.LogDebug("Cache invalidated for patient {PatientId}", patientId);
        return Task.CompletedTask;
    }
}
