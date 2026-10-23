using FastEndpoints;
using PatientMonitoring.Api.Contracts;
using PatientMonitoring.Api.Services;

namespace PatientMonitoring.Api.Endpoints.Patients;

public sealed class GetAllPatientsEndpoint(IPatientService patients)
    : EndpointWithoutRequest<IReadOnlyList<PatientResponse>>
{
    public override void Configure()
    {
        Get("/api/patients");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var includeInactive = Query<bool>("includeInactive", isRequired: false);
        var result = await patients.GetAllAsync(includeInactive, ct);
        await Send.OkAsync(result, ct);
    }
}
