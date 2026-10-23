using FastEndpoints;
using PatientMonitoring.Api.Contracts;
using PatientMonitoring.Api.Services;

namespace PatientMonitoring.Api.Endpoints.VitalSigns;

public sealed class GetPatientVitalsHistoryEndpoint(IVitalSignsService vitals)
    : EndpointWithoutRequest<IReadOnlyList<VitalSignsResponse>>
{
    public override void Configure()
    {
        Get("/api/patients/{patientId:guid}/vitals");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var patientId = Route<Guid>("patientId");
        var from = Query<DateTimeOffset?>("from", isRequired: false);
        var to = Query<DateTimeOffset?>("to", isRequired: false);
        var limit = Query<int>("limit", isRequired: false);
        if (limit <= 0) limit = 100;

        var history = await vitals.GetHistoryAsync(patientId, from, to, limit, ct);
        await Send.OkAsync(history, ct);
    }
}
