using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using PatientMonitoring.Api.Data;
using PatientMonitoring.Api.Services;

namespace PatientMonitoring.UnitTests;

public class DatabaseHealthCheckTests
{
    [Fact]
    public async Task CheckHealthAsync_WhenDatabaseConnects_ReturnsHealthy()
    {
        // Arrange
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        using var db = new AppDbContext(options);
        var healthCheck = new DatabaseHealthCheck(db);

        // Act
        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext());

        // Assert
        Assert.Equal(HealthStatus.Healthy, result.Status);
    }
}
