using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using PatientMonitoring.Api.Contracts;
using PatientMonitoring.Api.Models;
using PatientMonitoring.Api.Services.Caching;

namespace PatientMonitoring.UnitTests;

public class VitalSignsCacheTests
{
    private readonly MemoryVitalSignsCache _cache;

    public VitalSignsCacheTests()
    {
        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        _cache = new MemoryVitalSignsCache(memoryCache, NullLogger<MemoryVitalSignsCache>.Instance);
    }

    [Fact]
    public async Task GetLatestAsync_WhenKeyDoesNotExist_ReturnsNull()
    {
        // Act
        var result = await _cache.GetLatestAsync(Guid.NewGuid());

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task SetLatestAsync_AndGetLatestAsync_ReturnsCachedValue()
    {
        // Arrange
        var patientId = Guid.NewGuid();
        var vitals = new VitalSignsResponse(
            1, patientId, DateTime.UtcNow, DateTime.UtcNow, "dev1",
            75, 120, 80, 98.0, 36.6, 16, VitalStatus.Normal, [], "Петренко");

        // Act
        await _cache.SetLatestAsync(patientId, vitals);
        var cached = await _cache.GetLatestAsync(patientId);

        // Assert
        Assert.NotNull(cached);
        Assert.Equal(patientId, cached.PatientId);
        Assert.Equal(75, cached.HeartRate);
        Assert.Equal("Петренко", cached.PatientFullName);
    }

    [Fact]
    public async Task InvalidateAsync_RemovesCachedItem()
    {
        // Arrange
        var patientId = Guid.NewGuid();
        var vitals = new VitalSignsResponse(
            1, patientId, DateTime.UtcNow, DateTime.UtcNow, "dev1",
            75, 120, 80, 98.0, 36.6, 16, VitalStatus.Normal, [], "Петренко");
        await _cache.SetLatestAsync(patientId, vitals);

        // Act
        await _cache.InvalidateAsync(patientId);
        var cached = await _cache.GetLatestAsync(patientId);

        // Assert
        Assert.Null(cached);
    }
}
