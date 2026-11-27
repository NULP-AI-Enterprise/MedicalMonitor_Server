using MedicalMonitorServer.Contracts.Api;
using MedicalMonitorServer.Contracts.Models;
using MedicalMonitorServer.Core.Monitoring;
using MedicalMonitorServer.Hubs;
using MedicalMonitorServer.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MedicalMonitorServer.Tests;

/// <summary>
/// The history as the host really builds and runs it: registered the way Program.cs registers
/// it, resolved from the container, started as a hosted service. The recorder, the pipeline
/// and the database are three singletons that must be the same three everywhere - a second
/// recorder that nothing feeds would record nothing, without a single failing unit test.
/// </summary>
public sealed class HistoryWiringTests : IDisposable
{
    private const string Monitor = "10.10.46.48";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "mm-history-wiring-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private ServiceProvider BuildContainer(string? historyPath = "data/history.db")
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["History:Path"] = historyPath })
            .Build());
        services.AddSingleton<IHubContext<VitalsHub>>(new FakeHubContext());

        // As in Program.cs.
        services.AddSingleton<IPatientRegistry, PatientRegistry>();
        services.AddSingleton<IVitalsRecorder, VitalsRecorder>();
        services.AddSingleton(serviceProvider => PatientHistory.Open(
            serviceProvider.GetRequiredService<IConfiguration>()["History:Path"],
            _folder,
            serviceProvider.GetRequiredService<IVitalsRecorder>(),
            serviceProvider.GetRequiredService<ILogger<PatientHistory>>()));
        services.AddSingleton<IAlertStateService, AlertStateService>();
        services.AddSingleton<IMonitorBindings, MonitorBindings>();
        services.AddSingleton<MonitorRouter>();
        services.AddSingleton<FeedStatus>();
        services.AddSingleton<IMonitorPipeline, MonitorPipeline>();
        services.AddHostedService<HistoryRecorderService>();

        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private static HistoryRecorderService Recorder(IServiceProvider container)
    {
        var service = container.GetServices<IHostedService>().OfType<HistoryRecorderService>().Single();
        service.Interval = TimeSpan.FromMilliseconds(20);
        return service;
    }

    private static async Task<PatientInfoDto> AdmitOnMonitorAsync(IServiceProvider container)
    {
        var router = container.GetRequiredService<MonitorRouter>();
        await router.ResolveAsync(
            Monitor, new PatientInfo { PidPresent = true, PatientId = "321" }, hasReadings: true, CancellationToken.None);

        var outcome = await router.AdmitAsync("321", new PatientEntry("DA-1001", "Іваненко Іван", 4, "Adu", null), CancellationToken.None);
        Assert.Equal(AdmitOutcomeKind.Admitted, outcome.Kind);
        return outcome.Patient!;
    }

    /// <summary>A report of values from the monitor, through the router and the pipeline as the feed sends it.</summary>
    private static async Task ValuesAsync(IServiceProvider container, int heartRate)
    {
        var patientId = await container.GetRequiredService<MonitorRouter>()
            .ResolveAsync(Monitor, null, hasReadings: true, CancellationToken.None);
        await container.GetRequiredService<IMonitorPipeline>().IngestReadingsAsync(
            patientId!, new VitalReadings(HeartRate: heartRate, SpO2: 98), null, DateTimeOffset.UtcNow, CancellationToken.None, complete: true);
    }

    private static int RecordedSeconds(IServiceProvider container, string patientId)
    {
        var history = container.GetRequiredService<PatientHistory>();
        var patient = history.GetPatient(patientId)!;
        return history.GetVitals(patient.Num, 0, long.MaxValue, 100_000).Count;
    }

    [Fact]
    public async Task The_host_built_service_writes_what_the_pipeline_takes_in()
    {
        using var container = BuildContainer();
        var service = Recorder(container);
        await service.StartAsync(CancellationToken.None);
        try
        {
            var patient = await AdmitOnMonitorAsync(container);
            Assert.Equal(PatientHistory.Recording, container.GetRequiredService<PatientHistory>().Status);

            await ValuesAsync(container, 80);

            await Wait.UntilAsync(
                () => RecordedSeconds(container, patient.PatientId) > 0,
                "the running service writes the second to the database");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task What_came_in_since_the_last_tick_is_written_when_the_server_stops()
    {
        using var container = BuildContainer();
        var service = Recorder(container);
        service.Interval = TimeSpan.FromHours(1);          // the loop itself never gets to it
        await service.StartAsync(CancellationToken.None);

        var patient = await AdmitOnMonitorAsync(container);
        await ValuesAsync(container, 80);
        Assert.Equal(0, RecordedSeconds(container, patient.PatientId));

        await service.StopAsync(CancellationToken.None);

        Assert.Equal(1, RecordedSeconds(container, patient.PatientId));
    }

    [Fact]
    public async Task Starting_puts_the_admitted_patients_back_before_anything_else()
    {
        PatientInfoDto patient;
        using (var before = BuildContainer())
        {
            patient = await AdmitOnMonitorAsync(before);
        }

        using var after = BuildContainer();
        var service = Recorder(after);
        Assert.Empty(after.GetRequiredService<IPatientRegistry>().GetAll());

        // StartAsync has returned: the next hosted service - the feed - starts after this.
        await service.StartAsync(CancellationToken.None);
        try
        {
            Assert.Equal(patient, Assert.Single(after.GetRequiredService<IPatientRegistry>().GetAll()));
            Assert.Equal(patient.PatientId, after.GetRequiredService<IMonitorBindings>().Get(Monitor)!.PatientId);

            await ValuesAsync(after, 81);
            await Wait.UntilAsync(
                () => RecordedSeconds(after, patient.PatientId) > 0,
                "the patient put back is recorded again");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task With_no_path_the_history_is_off_and_the_server_runs_as_before()
    {
        using var container = BuildContainer(historyPath: "");
        var service = Recorder(container);
        await service.StartAsync(CancellationToken.None);
        try
        {
            var router = container.GetRequiredService<MonitorRouter>();
            await router.ResolveAsync(
                Monitor, new PatientInfo { PidPresent = true, PatientId = "321" }, hasReadings: true, CancellationToken.None);
            await ValuesAsync(container, 80);

            Assert.Equal(PatientHistory.Off, container.GetRequiredService<PatientHistory>().Status);
            Assert.Equal(
                AdmitOutcomeKind.HistoryUnavailable,
                (await router.AdmitAsync("321", new PatientEntry("DA-1", "", 1, "Adu", null), CancellationToken.None)).Kind);
            Assert.Equal(80, container.GetRequiredService<IPatientRegistry>().GetReadings("321")!.HeartRate);
            Assert.False(Directory.Exists(Path.Combine(_folder, "data")));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }
}
