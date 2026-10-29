using Microsoft.EntityFrameworkCore;

namespace PatientMonitoring.Api.Data;

/// <summary>
/// Creates or migrates the database in the background. A database that is down must not stop the API from
/// listening on port 5000: the station works from its roster, and the migration is retried until it succeeds.
/// </summary>
public sealed class DatabaseMigrationService(
    IServiceScopeFactory scopeFactory,
    ILogger<DatabaseMigrationService> logger) : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                if (db.Database.IsRelational())
                {
                    await db.Database.MigrateAsync(stoppingToken);
                }
                else
                {
                    await db.Database.EnsureCreatedAsync(stoppingToken);
                }

                logger.LogInformation("Database is ready.");
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Database is not available; the migration is retried in {Seconds} s.", RetryDelay.TotalSeconds);
            }

            try
            {
                await Task.Delay(RetryDelay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
