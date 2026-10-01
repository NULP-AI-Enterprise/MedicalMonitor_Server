using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using PatientMonitoring.Api.Data;
using PatientMonitoring.Api.Models;

namespace PatientMonitoring.IntegrationTests;

public class PatientMonitoringWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _dbName = Guid.NewGuid().ToString();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Default", $"InMemory:{_dbName}");
        builder.UseEnvironment("Development");
    }

    public async Task<Guid> SeedPatientAsync(string fullName, string ward = "101", string bed = "A")
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var patient = new Patient
        {
            Id = Guid.NewGuid(),
            FullName = fullName,
            Ward = ward,
            Bed = bed,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        db.Patients.Add(patient);
        await db.SaveChangesAsync();

        return patient.Id;
    }
}
