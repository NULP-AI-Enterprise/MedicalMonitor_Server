using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using PatientMonitoring.Api.Contracts;

namespace PatientMonitoring.Api.Services.Caching;

public sealed class DistributedVitalSignsCache(
    IDistributedCache distributedCache,
    ILogger<DistributedVitalSignsCache> logger) : IVitalSignsCache
{
    private static readonly DistributedCacheEntryOptions DefaultOptions = new()
    {
        SlidingExpiration = TimeSpan.FromMinutes(10)
    };

    private static string CacheKey(Guid patientId) => $"vitals:latest:{patientId}";

    public async Task<VitalSignsResponse?> GetLatestAsync(Guid patientId, CancellationToken cancellationToken = default)
    {
        try
        {
            var cachedData = await distributedCache.GetStringAsync(CacheKey(patientId), cancellationToken);
            if (string.IsNullOrEmpty(cachedData))
            {
                logger.LogDebug("Distributed cache miss for patient {PatientId}", patientId);
                return null;
            }

            var vitals = JsonSerializer.Deserialize<VitalSignsResponse>(cachedData);
            logger.LogDebug("Distributed cache hit for patient {PatientId}", patientId);
            return vitals;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error reading from distributed cache for patient {PatientId}. Falling back to db.", patientId);
            return null;
        }
    }

    public async Task SetLatestAsync(Guid patientId, VitalSignsResponse vitals, CancellationToken cancellationToken = default)
    {
        try
        {
            var json = JsonSerializer.Serialize(vitals);
            await distributedCache.SetStringAsync(CacheKey(patientId), json, DefaultOptions, cancellationToken);
            logger.LogDebug("Distributed cache updated for patient {PatientId}", patientId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error writing to distributed cache for patient {PatientId}", patientId);
        }
    }

    public async Task InvalidateAsync(Guid patientId, CancellationToken cancellationToken = default)
    {
        try
        {
            await distributedCache.RemoveAsync(CacheKey(patientId), cancellationToken);
            logger.LogDebug("Distributed cache invalidated for patient {PatientId}", patientId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error invalidating distributed cache for patient {PatientId}", patientId);
        }
    }
}
