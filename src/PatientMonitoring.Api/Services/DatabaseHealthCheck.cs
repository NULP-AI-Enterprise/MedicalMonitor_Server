using Microsoft.Extensions.Diagnostics.HealthChecks;
using PatientMonitoring.Api.Data;

namespace PatientMonitoring.Api.Services;

public sealed class DatabaseHealthCheck(AppDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var canConnect = await db.Database.CanConnectAsync(cancellationToken);
            return canConnect
                ? HealthCheckResult.Healthy("PostgreSQL database is accessible.")
                : HealthCheckResult.Unhealthy("Cannot connect to PostgreSQL database.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL connection failure.", ex);
        }
    }
}
