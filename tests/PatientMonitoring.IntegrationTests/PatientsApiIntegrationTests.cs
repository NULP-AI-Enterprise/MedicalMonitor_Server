using System.Net;
using System.Net.Http.Json;
using PatientMonitoring.Api.Contracts;

namespace PatientMonitoring.IntegrationTests;

public class PatientsApiIntegrationTests : IClassFixture<PatientMonitoringWebApplicationFactory>
{
    private readonly HttpClient _client;

    public PatientsApiIntegrationTests(PatientMonitoringWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task CreatePatient_ReturnsCreatedAndCanBeRetrieved()
    {
        var createRequest = new CreatePatientRequest
        {
            FullName = "Олександр Коваленко",
            DateOfBirth = new DateOnly(1985, 4, 12),
            Ward = "204",
            Bed = "B"
        };

        var postResponse = await _client.PostAsJsonAsync("/api/patients", createRequest);
        Assert.Equal(HttpStatusCode.Created, postResponse.StatusCode);

        var createdPatient = await postResponse.Content.ReadFromJsonAsync<PatientResponse>();
        Assert.NotNull(createdPatient);
        Assert.Equal("Олександр Коваленко", createdPatient.FullName);
        Assert.Equal("204", createdPatient.Ward);
        Assert.Equal("B", createdPatient.Bed);
        Assert.True(createdPatient.IsActive);

        // Verify GET /api/patients/{id}
        var getResponse = await _client.GetAsync($"/api/patients/{createdPatient.Id}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);

        var fetchedPatient = await getResponse.Content.ReadFromJsonAsync<PatientResponse>();
        Assert.NotNull(fetchedPatient);
        Assert.Equal(createdPatient.Id, fetchedPatient.Id);
    }

    [Fact]
    public async Task GetAllPatients_ReturnsList()
    {
        var response = await _client.GetAsync("/api/patients");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var list = await response.Content.ReadFromJsonAsync<List<PatientResponse>>();
        Assert.NotNull(list);
    }

    [Fact]
    public async Task UpdatePatient_UpdatesWardAndBed()
    {
        // Create patient
        var createRes = await _client.PostAsJsonAsync("/api/patients", new CreatePatientRequest
        {
            FullName = "Марія Шевчук",
            Ward = "101",
            Bed = "A"
        });
        var created = await createRes.Content.ReadFromJsonAsync<PatientResponse>();
        Assert.NotNull(created);

        // Update
        var updateRequest = new UpdatePatientRequest
        {
            FullName = "Марія Шевчук",
            Ward = "105",
            Bed = "C"
        };
        var putRes = await _client.PutAsJsonAsync($"/api/patients/{created.Id}", updateRequest);
        Assert.Equal(HttpStatusCode.OK, putRes.StatusCode);

        var updated = await putRes.Content.ReadFromJsonAsync<PatientResponse>();
        Assert.NotNull(updated);
        Assert.Equal("105", updated.Ward);
        Assert.Equal("C", updated.Bed);
    }

    [Fact]
    public async Task DeactivatePatient_SetsIsActiveToFalse()
    {
        // Create patient
        var createRes = await _client.PostAsJsonAsync("/api/patients", new CreatePatientRequest
        {
            FullName = "Дмитро Мельник",
            Ward = "102"
        });
        var created = await createRes.Content.ReadFromJsonAsync<PatientResponse>();
        Assert.NotNull(created);

        // Deactivate (DELETE)
        var deleteRes = await _client.DeleteAsync($"/api/patients/{created.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleteRes.StatusCode);

        // Verify patient is now inactive
        var getRes = await _client.GetAsync($"/api/patients/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, getRes.StatusCode);
        var fetched = await getRes.Content.ReadFromJsonAsync<PatientResponse>();
        Assert.NotNull(fetched);
        Assert.False(fetched.IsActive);
    }
}
