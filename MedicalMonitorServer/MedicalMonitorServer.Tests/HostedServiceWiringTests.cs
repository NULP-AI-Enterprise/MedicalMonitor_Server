using MedicalMonitorServer.Contracts.Api;
using MedicalMonitorServer.Contracts.Models;
using MedicalMonitorServer.Core.Monitoring;
using MedicalMonitorServer.Hubs;
using MedicalMonitorServer.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MedicalMonitorServer.Tests;

/// <summary>
/// The silence check as the host really builds and runs it: registered the way
/// Program.cs registers it, resolved from the container, started as a hosted
/// service. A class that works when a test constructs it by hand can still be
/// wired wrongly, and then it does nothing in production without a single
/// failing test — which is exactly what happened to DeviceService once.
/// </summary>
public class HostedServiceWiringTests
{
    private static ServiceProvider BuildContainer(string staleAfterSeconds)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Health:StaleAfterSeconds"] = staleAfterSeconds,
            })
            .Build());
        services.AddSingleton<IHubContext<VitalsHub>>(new FakeHubContext());

        // As in Program.cs.
        services.AddSingleton<IPatientRegistry, PatientRegistry>();
        services.AddSingleton<IAlertStateService, AlertStateService>();
        services.AddSingleton<IMonitorBindings, MonitorBindings>();
        services.AddSingleton<MonitorRouter>();
        services.AddSingleton<FeedStatus>();
        services.AddSingleton<IMonitorPipeline, MonitorPipeline>();
        services.AddHostedService<MonitorSilenceService>();

        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private static MonitorSilenceService SilenceService(IServiceProvider container)
    {
        var service = container.GetServices<IHostedService>().OfType<MonitorSilenceService>().Single();
        service.SweepInterval = TimeSpan.FromMilliseconds(20);
        return service;
    }

    /// <summary>Admits P-1 on a monitor and places the monitor's last message in the past.</summary>
    private static async Task MonitorLastHeardAsync(IServiceProvider container, TimeSpan ago)
    {
        await container.GetRequiredService<MonitorRouter>().ResolveAsync(
            "10.0.0.7",
            new PatientInfo { PidPresent = true, PatientId = "P-1", LastName = "Doe" },
            hasReadings: true,
            CancellationToken.None);

        container.GetRequiredService<IMonitorBindings>().Report(
            "10.0.0.7", "P-1", 0, hasReadings: true, DateTimeOffset.UtcNow - ago);
    }

    private static bool SilenceAlertIsUp(IServiceProvider container) =>
        container.GetRequiredService<IAlertStateService>().GetActiveAlerts()
            .Any(a => a.AlertId == AlertDto.BuildMonitorSilentId("P-1"));

    [Fact]
    public async Task The_host_built_service_flags_a_monitor_that_went_silent()
    {
        using var container = BuildContainer(staleAfterSeconds: "2");
        var service = SilenceService(container);
        await MonitorLastHeardAsync(container, ago: TimeSpan.FromSeconds(10));

        await service.StartAsync(CancellationToken.None);
        try
        {
            await Wait.UntilAsync(() => SilenceAlertIsUp(container), "the running service raises the alert");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task The_threshold_comes_from_configuration()
    {
        // Ten seconds of silence is within a 60 s threshold.
        using var container = BuildContainer(staleAfterSeconds: "60");
        var service = SilenceService(container);
        await MonitorLastHeardAsync(container, ago: TimeSpan.FromSeconds(10));

        await service.StartAsync(CancellationToken.None);
        try
        {
            await Task.Delay(300);                      // many sweeps
            Assert.False(SilenceAlertIsUp(container));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    public async Task A_nonsensical_threshold_falls_back_to_thirty_seconds(string configured)
    {
        // Zero would flag every monitor on every sweep.
        using var container = BuildContainer(configured);
        var service = SilenceService(container);
        await MonitorLastHeardAsync(container, ago: TimeSpan.FromSeconds(10));

        await service.StartAsync(CancellationToken.None);
        try
        {
            await Task.Delay(300);
            Assert.False(SilenceAlertIsUp(container));  // 10 s is not past 30 s

            container.GetRequiredService<IMonitorBindings>().Report(
                "10.0.0.7", "P-1", 0, hasReadings: true, DateTimeOffset.UtcNow.AddSeconds(-40));
            await Wait.UntilAsync(() => SilenceAlertIsUp(container), "40 s of silence is flagged");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }
}
