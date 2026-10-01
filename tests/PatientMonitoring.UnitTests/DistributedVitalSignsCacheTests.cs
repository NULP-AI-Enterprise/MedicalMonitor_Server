using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PatientMonitoring.Api.Contracts;
using PatientMonitoring.Api.Models;
using PatientMonitoring.Api.Services.Caching;

namespace PatientMonitoring.UnitTests;

public class DistributedVitalSignsCacheTests
{
    private readonly Mock<IDistributedCache> _mockCache;
    private readonly DistributedVitalSignsCache _cache;

    public DistributedVitalSignsCacheTests()
    {
        _mockCache = new Mock<IDistributedCache>();
        _cache = new DistributedVitalSignsCache(_mockCache.Object, NullLogger<DistributedVitalSignsCache>.Instance);
    }

    [Fact]
    public async Task GetLatestAsync_WhenCacheMiss_ReturnsNull()
    {
        var patientId = Guid.NewGuid();
        _mockCache.Setup(c => c.GetAsync($"vitals:latest:{patientId}", It.IsAny<CancellationToken>()))
            .ReturnsAsync((byte[]?)null);

        var result = await _cache.GetLatestAsync(patientId);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetLatestAsync_WhenCacheHit_ReturnsDeserializedVitals()
    {
        var patientId = Guid.NewGuid();
        var vitals = new VitalSignsResponse(
            1,
            patientId,
            DateTime.UtcNow,
            DateTime.UtcNow,
            "DEV-1",
            75,
            120,
            80,
            98,
            36.6,
            16,
            VitalStatus.Normal,
            [],
            "Оксана Бойко"
        );

        var json = JsonSerializer.Serialize(vitals);
        var bytes = Encoding.UTF8.GetBytes(json);

        _mockCache.Setup(c => c.GetAsync($"vitals:latest:{patientId}", It.IsAny<CancellationToken>()))
            .ReturnsAsync(bytes);

        var result = await _cache.GetLatestAsync(patientId);

        Assert.NotNull(result);
        Assert.Equal(patientId, result.PatientId);
        Assert.Equal("Оксана Бойко", result.PatientFullName);
        Assert.Equal(75, result.HeartRate);
    }

    [Fact]
    public async Task SetLatestAsync_SerializesAndSavesToCache()
    {
        var patientId = Guid.NewGuid();
        var vitals = new VitalSignsResponse(
            1,
            patientId,
            DateTime.UtcNow,
            DateTime.UtcNow,
            "DEV-1",
            75,
            120,
            80,
            98,
            36.6,
            16,
            VitalStatus.Normal,
            [],
            "Оксана Бойко"
        );

        await _cache.SetLatestAsync(patientId, vitals);

        _mockCache.Verify(c => c.SetAsync(
            $"vitals:latest:{patientId}",
            It.IsAny<byte[]>(),
            It.IsAny<DistributedCacheEntryOptions>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task InvalidateAsync_RemovesKeyFromCache()
    {
        var patientId = Guid.NewGuid();

        await _cache.InvalidateAsync(patientId);

        _mockCache.Verify(c => c.RemoveAsync($"vitals:latest:{patientId}", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CacheOperations_WhenDistributedCacheThrows_HandledGracefully()
    {
        var patientId = Guid.NewGuid();
        _mockCache.Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Redis connection timed out"));
        _mockCache.Setup(c => c.SetAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<DistributedCacheEntryOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Redis down"));
        _mockCache.Setup(c => c.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Redis down"));

        var vitals = new VitalSignsResponse(
            1, patientId, DateTime.UtcNow, DateTime.UtcNow, null, null, null, null, null, null, null, VitalStatus.Normal, []);

        var getResult = await _cache.GetLatestAsync(patientId);
        Assert.Null(getResult);

        // Set and Invalidate should not throw
        await _cache.SetLatestAsync(patientId, vitals);
        await _cache.InvalidateAsync(patientId);
    }
}
