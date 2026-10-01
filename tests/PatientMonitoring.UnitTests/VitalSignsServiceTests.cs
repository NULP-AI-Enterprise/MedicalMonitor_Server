using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using PatientMonitoring.Api.Contracts;
using PatientMonitoring.Api.Data;
using PatientMonitoring.Api.Models;
using PatientMonitoring.Api.Services;
using PatientMonitoring.Api.Services.Alerting;
using PatientMonitoring.Api.Services.Caching;
using PatientMonitoring.Api.Services.Relay;

namespace PatientMonitoring.UnitTests;

public class VitalSignsServiceTests
{
    private static AppDbContext CreateInMemoryDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        return new AppDbContext(options);
    }

    private static VitalSignsService CreateService(
        AppDbContext db,
        IVitalSignsAnalyzer? analyzer = null,
        Mock<IPatientNotifier>? mockNotifier = null,
        Mock<IVitalSignsCache>? mockCache = null,
        Mock<IAlertDispatcher>? mockAlerts = null,
        Mock<IRealtimeDataRelay>? mockRelay = null,
        Mock<ISseStreamService>? mockSse = null)
    {
        return new VitalSignsService(
            db,
            analyzer ?? new VitalSignsAnalyzer(Options.Create(new VitalThresholds())),
            (mockNotifier ?? new Mock<IPatientNotifier>()).Object,
            (mockCache ?? new Mock<IVitalSignsCache>()).Object,
            (mockAlerts ?? new Mock<IAlertDispatcher>()).Object,
            (mockRelay ?? new Mock<IRealtimeDataRelay>()).Object,
            (mockSse ?? new Mock<ISseStreamService>()).Object,
            NullLogger<VitalSignsService>.Instance);
    }

    [Fact]
    public async Task RecordAsync_WhenPatientNotFound_ReturnsNull()
    {
        // Arrange
        using var db = CreateInMemoryDbContext();
        var mockNotifier = new Mock<IPatientNotifier>();
        var mockAlerts = new Mock<IAlertDispatcher>();
        var service = CreateService(db, mockNotifier: mockNotifier, mockAlerts: mockAlerts);

        // Act
        var result = await service.RecordAsync(Guid.NewGuid(), new RecordVitalSignsRequest { HeartRate = 75 });

        // Assert
        Assert.Null(result);
        mockNotifier.Verify(n => n.NotifyVitalSignsAsync(It.IsAny<VitalSignsResponse>(), It.IsAny<CancellationToken>()), Times.Never);
        mockAlerts.Verify(a => a.DispatchAsync(It.IsAny<PatientAlertNotification>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RecordAsync_WhenNormal_SavesVitalsAndUpdatesCacheAndRelays()
    {
        // Arrange
        using var db = CreateInMemoryDbContext();
        var patientId = Guid.NewGuid();
        db.Patients.Add(new Patient { Id = patientId, FullName = "Петренко Петро", IsActive = true, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var mockNotifier = new Mock<IPatientNotifier>();
        var mockCache = new Mock<IVitalSignsCache>();
        var mockAlerts = new Mock<IAlertDispatcher>();
        var mockRelay = new Mock<IRealtimeDataRelay>();
        var mockSse = new Mock<ISseStreamService>();
        var service = CreateService(db, mockNotifier: mockNotifier, mockCache: mockCache, mockAlerts: mockAlerts, mockRelay: mockRelay, mockSse: mockSse);

        var request = new RecordVitalSignsRequest
        {
            HeartRate = 72,
            SystolicBloodPressure = 120,
            DiastolicBloodPressure = 80,
            OxygenSaturation = 98.0
        };

        // Act
        var result = await service.RecordAsync(patientId, request);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(VitalStatus.Normal, result.Status);
        Assert.Equal("Петренко Петро", result.PatientFullName);

        mockCache.Verify(c => c.SetLatestAsync(patientId, It.Is<VitalSignsResponse>(v => v.PatientId == patientId), It.IsAny<CancellationToken>()), Times.Once);
        mockNotifier.Verify(n => n.NotifyVitalSignsAsync(It.Is<VitalSignsResponse>(v => v.PatientId == patientId), It.IsAny<CancellationToken>()), Times.Once);
        mockSse.Verify(s => s.Broadcast(It.Is<VitalSignsResponse>(v => v.PatientId == patientId)), Times.Once);
        mockRelay.Verify(r => r.RelayVitalSignsAsync(It.Is<VitalSignsResponse>(v => v.PatientId == patientId), It.IsAny<CancellationToken>()), Times.Once);
        mockAlerts.Verify(a => a.DispatchAsync(It.IsAny<PatientAlertNotification>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RecordAsync_WhenCritical_DispatchesExternalAlert()
    {
        // Arrange
        using var db = CreateInMemoryDbContext();
        var patientId = Guid.NewGuid();
        db.Patients.Add(new Patient { Id = patientId, FullName = "Коваленко Ольга", IsActive = true, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var mockNotifier = new Mock<IPatientNotifier>();
        var mockCache = new Mock<IVitalSignsCache>();
        var mockAlerts = new Mock<IAlertDispatcher>();
        var service = CreateService(db, mockNotifier: mockNotifier, mockCache: mockCache, mockAlerts: mockAlerts);

        var request = new RecordVitalSignsRequest
        {
            HeartRate = 145 // Critical (> 130)
        };

        // Act
        var result = await service.RecordAsync(patientId, request);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(VitalStatus.Critical, result.Status);
        Assert.Single(result.Alerts);

        mockNotifier.Verify(n => n.NotifyVitalSignsAsync(It.IsAny<VitalSignsResponse>(), It.IsAny<CancellationToken>()), Times.Once);
        mockNotifier.Verify(n => n.NotifyAlertAsync(It.Is<PatientAlertNotification>(a => a.PatientId == patientId && a.Status == VitalStatus.Critical), It.IsAny<CancellationToken>()), Times.Once);
        mockAlerts.Verify(a => a.DispatchAsync(It.Is<PatientAlertNotification>(a => a.PatientId == patientId && a.Status == VitalStatus.Critical), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetLatestAsync_WhenCacheHit_ReturnsCachedWithoutQueryingDb()
    {
        // Arrange
        using var db = CreateInMemoryDbContext();
        var patientId = Guid.NewGuid();
        var cachedVitals = new VitalSignsResponse(1, patientId, DateTime.UtcNow, DateTime.UtcNow, "cache-device", 75, 120, 80, 98.0, 36.6, 16, VitalStatus.Normal, []);

        var mockCache = new Mock<IVitalSignsCache>();
        mockCache.Setup(c => c.GetLatestAsync(patientId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(cachedVitals);

        var service = CreateService(db, mockCache: mockCache);

        // Act
        var result = await service.GetLatestAsync(patientId);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("cache-device", result.DeviceId);
        Assert.Equal(75, result.HeartRate);
    }

    [Fact]
    public async Task GetHistoryAsync_AppliesLimitAndOrdering()
    {
        // Arrange
        using var db = CreateInMemoryDbContext();
        var patientId = Guid.NewGuid();
        db.Patients.Add(new Patient { Id = patientId, FullName = "Тест", IsActive = true, CreatedAt = DateTime.UtcNow });

        for (int i = 0; i < 10; i++)
        {
            db.VitalSigns.Add(new VitalSign
            {
                PatientId = patientId,
                HeartRate = 70 + i,
                RecordedAt = DateTime.UtcNow.AddMinutes(i),
                ReceivedAt = DateTime.UtcNow.AddMinutes(i)
            });
        }
        await db.SaveChangesAsync();

        var service = CreateService(db);

        // Act
        var history = await service.GetHistoryAsync(patientId, from: null, to: null, limit: 5);

        // Assert
        Assert.Equal(5, history.Count);
        // Latest first
        Assert.True(history[0].RecordedAt > history[1].RecordedAt);
    }

    [Fact]
    public async Task RecordBatchAsync_PersistsAllItemsAndNotifiesLatest()
    {
        // Arrange
        using var db = CreateInMemoryDbContext();
        var patientId = Guid.NewGuid();
        db.Patients.Add(new Patient { Id = patientId, FullName = "Дмитренко", IsActive = true, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var mockNotifier = new Mock<IPatientNotifier>();
        var mockCache = new Mock<IVitalSignsCache>();
        var mockAlerts = new Mock<IAlertDispatcher>();
        var mockRelay = new Mock<IRealtimeDataRelay>();
        var mockSse = new Mock<ISseStreamService>();
        var service = CreateService(db, mockNotifier: mockNotifier, mockCache: mockCache, mockAlerts: mockAlerts, mockRelay: mockRelay, mockSse: mockSse);

        var batchRequest = new RecordVitalSignsBatchRequest
        {
            Items = new List<RecordVitalSignsRequest>
            {
                new() { HeartRate = 75, RecordedAt = DateTimeOffset.UtcNow.AddMinutes(-10) },
                new() { HeartRate = 80, RecordedAt = DateTimeOffset.UtcNow.AddMinutes(-5) },
                new() { HeartRate = 135, RecordedAt = DateTimeOffset.UtcNow } // Critical
            }
        };

        // Act
        var result = await service.RecordBatchAsync(patientId, batchRequest);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(3, result.InsertedCount);
        Assert.NotNull(result.LatestRecorded);
        Assert.Equal(135, result.LatestRecorded.HeartRate);
        Assert.Equal(VitalStatus.Critical, result.LatestRecorded.Status);

        var countInDb = await db.VitalSigns.CountAsync(v => v.PatientId == patientId);
        Assert.Equal(3, countInDb);

        mockCache.Verify(c => c.SetLatestAsync(patientId, It.Is<VitalSignsResponse>(v => v.HeartRate == 135), It.IsAny<CancellationToken>()), Times.Once);
        mockNotifier.Verify(n => n.NotifyVitalSignsAsync(It.Is<VitalSignsResponse>(v => v.HeartRate == 135), It.IsAny<CancellationToken>()), Times.Once);
        mockSse.Verify(s => s.Broadcast(It.Is<VitalSignsResponse>(v => v.HeartRate == 135)), Times.Once);
        mockRelay.Verify(r => r.RelayBatchAsync(It.Is<RecordVitalSignsBatchResponse>(b => b.InsertedCount == 3), It.IsAny<CancellationToken>()), Times.Once);
        mockAlerts.Verify(a => a.DispatchAsync(It.Is<PatientAlertNotification>(a => a.Status == VitalStatus.Critical), It.IsAny<CancellationToken>()), Times.Once);
    }
}
