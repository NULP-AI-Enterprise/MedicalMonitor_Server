using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Moq;
using PatientMonitoring.Api.Contracts;
using PatientMonitoring.Api.Data;
using PatientMonitoring.Api.Models;
using PatientMonitoring.Api.Services;
using PatientMonitoring.Api.Services.Caching;

namespace PatientMonitoring.UnitTests;

public class PatientServiceTests
{
    private static AppDbContext CreateInMemoryDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        return new AppDbContext(options);
    }

    private static IVitalSignsAnalyzer CreateAnalyzer()
    {
        return new VitalSignsAnalyzer(Options.Create(new VitalThresholds()));
    }

    private static IVitalSignsCache CreateMockCache()
    {
        var mock = new Mock<IVitalSignsCache>();
        return mock.Object;
    }

    [Fact]
    public async Task CreateAsync_PersistsPatientAndReturnsResponse()
    {
        // Arrange
        using var db = CreateInMemoryDbContext();
        var service = new PatientService(db, CreateAnalyzer(), CreateMockCache());
        var request = new CreatePatientRequest
        {
            FullName = "Іваненко Іван",
            Ward = "Палата 1",
            Bed = "A1"
        };

        // Act
        var result = await service.CreateAsync(request);

        // Assert
        Assert.NotEqual(Guid.Empty, result.Id);
        Assert.Equal("Іваненко Іван", result.FullName);
        Assert.True(result.IsActive);
        Assert.Null(result.LatestVitals);

        var inDb = await db.Patients.FindAsync(result.Id);
        Assert.NotNull(inDb);
        Assert.Equal("Іваненко Іван", inDb.FullName);
    }

    [Fact]
    public async Task GetAllAsync_WhenIncludeInactiveIsFalse_FiltersOutInactivePatients()
    {
        // Arrange
        using var db = CreateInMemoryDbContext();
        var activePatient = new Patient { Id = Guid.NewGuid(), FullName = "Active 1", IsActive = true, CreatedAt = DateTime.UtcNow };
        var inactivePatient = new Patient { Id = Guid.NewGuid(), FullName = "Inactive 2", IsActive = false, CreatedAt = DateTime.UtcNow };
        db.Patients.AddRange(activePatient, inactivePatient);
        await db.SaveChangesAsync();

        var service = new PatientService(db, CreateAnalyzer(), CreateMockCache());

        // Act
        var activeOnly = await service.GetAllAsync(includeInactive: false);
        var all = await service.GetAllAsync(includeInactive: true);

        // Assert
        Assert.Single(activeOnly);
        Assert.Equal(activePatient.Id, activeOnly[0].Id);
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async Task GetByIdAsync_ReturnsPatientWithLatestVitals()
    {
        // Arrange
        using var db = CreateInMemoryDbContext();
        var patientId = Guid.NewGuid();
        var patient = new Patient { Id = patientId, FullName = "Тест Тестович", IsActive = true, CreatedAt = DateTime.UtcNow };
        var vital1 = new VitalSign
        {
            PatientId = patientId,
            RecordedAt = DateTime.UtcNow.AddMinutes(-10),
            ReceivedAt = DateTime.UtcNow.AddMinutes(-10),
            HeartRate = 70,
            Status = VitalStatus.Normal
        };
        var vital2 = new VitalSign
        {
            PatientId = patientId,
            RecordedAt = DateTime.UtcNow,
            ReceivedAt = DateTime.UtcNow,
            HeartRate = 120,
            Status = VitalStatus.Warning
        };
        db.Patients.Add(patient);
        db.VitalSigns.AddRange(vital1, vital2);
        await db.SaveChangesAsync();

        var service = new PatientService(db, CreateAnalyzer(), CreateMockCache());

        // Act
        var result = await service.GetByIdAsync(patientId);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(patientId, result.Id);
        Assert.NotNull(result.LatestVitals);
        Assert.Equal(120, result.LatestVitals.HeartRate);
        Assert.Equal("Тест Тестович", result.LatestVitals.PatientFullName);
    }

    [Fact]
    public async Task DeactivateAsync_SetsIsActiveToFalseAndInvalidatesCache()
    {
        // Arrange
        using var db = CreateInMemoryDbContext();
        var patientId = Guid.NewGuid();
        db.Patients.Add(new Patient { Id = patientId, FullName = "Пацієнт", IsActive = true, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var mockCache = new Mock<IVitalSignsCache>();
        var service = new PatientService(db, CreateAnalyzer(), mockCache.Object);

        // Act
        var deactivated = await service.DeactivateAsync(patientId);

        // Assert
        Assert.True(deactivated);
        var inDb = await db.Patients.FindAsync(patientId);
        Assert.NotNull(inDb);
        Assert.False(inDb.IsActive);
        mockCache.Verify(c => c.InvalidateAsync(patientId, It.IsAny<CancellationToken>()), Times.Once);
    }
}
