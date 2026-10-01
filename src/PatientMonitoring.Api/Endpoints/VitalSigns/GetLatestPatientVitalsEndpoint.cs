using FastEndpoints;
using PatientMonitoring.Api.Contracts;
using PatientMonitoring.Api.Services;

namespace PatientMonitoring.Api.Endpoints.VitalSigns;

public sealed class GetLatestPatientVitalsEndpoint(IVitalSignsService vitals)
    : EndpointWithoutRequest<VitalSignsResponse>
{
    public override void Configure()
    {
        Get("/api/patients/{patientId:guid}/vitals/latest");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var patientId = Route<Guid>("patientId");
        var result = await vitals.GetLatestAsync(patientId, ct);

        if (result is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(result, ct);
    }
}
