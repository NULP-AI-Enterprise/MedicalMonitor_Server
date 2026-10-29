using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Monitoring.Shared.DTOs;
using PatientMonitoring.Api.Data;
using PatientMonitoring.Api.Services.CentralStation;

namespace PatientMonitoring.IntegrationTests;

public class PatientTypePersistenceIntegrationTests : IClassFixture<PatientMonitoringWebApplicationFactory>
{
    private readonly PatientMonitoringWebApplicationFactory _factory;

    public PatientTypePersistenceIntegrationTests(PatientMonitoringWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task PatientType_IsStoredWithThePatient()
    {
        var client = _factory.CreateClient();
        var name = $"Type Stored {Guid.NewGuid():N}";

        var response = await client.PostAsJsonAsync("/api/v1/patients",
            new PatientInfoDto($"TYPESTORE_{Guid.NewGuid():N}", name, 81, null, "Ped"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("Ped", await StoredTypeAsync(name));
    }

    [Fact]
    public async Task PatientType_IsUpdatedInStorage()
    {
        var client = _factory.CreateClient();
        var id = $"TYPEUPD_{Guid.NewGuid():N}";
        var name = $"Type Updated {Guid.NewGuid():N}";
        await client.PostAsJsonAsync("/api/v1/patients", new PatientInfoDto(id, name, 82, null, "Adu"));

        var response = await client.PutAsJsonAsync($"/api/v1/patients/{id}", new PatientInfoDto(id, name, 82, null, "Neo"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Neo", await StoredTypeAsync(name));
    }

    [Fact]
    public async Task PatientType_IsRestoredAfterRestart()
    {
        var client = _factory.CreateClient();
        var id = $"TYPEREST_{Guid.NewGuid():N}";
        var response = await client.PostAsJsonAsync("/api/v1/patients",
            new PatientInfoDto(id, "Restart Type", 83, null, "Neo"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        // A restarted station starts with an empty roster and reads the stored patients from the database.
        var restarted = ActivatorUtilities.CreateInstance<CentralStationService>(_factory.Services);
        var patients = await restarted.GetPatientsAsync();

        Assert.Equal("Neo", Assert.Single(patients, p => p.PatientId == id).PatientType);
    }

    [Fact]
    public async Task PatientRegisteredAgainAfterDischarge_IsStoredAsActive()
    {
        var client = _factory.CreateClient();
        var id = $"TYPEAGAIN_{Guid.NewGuid():N}";
        var name = $"Registered Again {Guid.NewGuid():N}";
        await client.PostAsJsonAsync("/api/v1/patients", new PatientInfoDto(id, name, 84, null, "Adu"));
        var deleted = await client.DeleteAsync($"/api/v1/patients/{id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        var again = await client.PostAsJsonAsync("/api/v1/patients",
            new PatientInfoDto(id, name, 84, "New diagnosis", "Ped"));

        Assert.Equal(HttpStatusCode.Created, again.StatusCode);
        using var scope = _factory.Services.CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .Patients.SingleAsync(p => p.FullName == name);
        Assert.True(stored.IsActive);
        Assert.Equal("Ped", stored.PatientType);
        Assert.Equal(id, stored.StationPatientId);
    }

    private async Task<string> StoredTypeAsync(string fullName)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return (await db.Patients.SingleAsync(p => p.FullName == fullName)).PatientType;
    }
}
