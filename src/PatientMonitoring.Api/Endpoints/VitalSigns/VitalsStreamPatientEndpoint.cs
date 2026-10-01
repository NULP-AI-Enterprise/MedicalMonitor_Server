using System.Text.Encodings.Web;
using System.Text.Json;
using FastEndpoints;
using Microsoft.AspNetCore.Http;
using PatientMonitoring.Api.Services.Relay;

namespace PatientMonitoring.Api.Endpoints.VitalSigns;

public sealed class VitalsStreamPatientEndpoint(ISseStreamService sseService)
    : EndpointWithoutRequest
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public override void Configure()
    {
        Get("/api/patients/{patientId:guid}/vitals/stream");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var patientId = Route<Guid>("patientId");

        HttpContext.Response.Headers.Append("Content-Type", "text/event-stream");
        HttpContext.Response.Headers.Append("Cache-Control", "no-cache");
        HttpContext.Response.Headers.Append("Connection", "keep-alive");

        await HttpContext.Response.Body.FlushAsync(ct);

        await foreach (var item in sseService.SubscribeAsync(patientId, ct))
        {
            var json = JsonSerializer.Serialize(item, JsonOptions);
            var message = $"data: {json}\n\n";
            await HttpContext.Response.WriteAsync(message, ct);
            await HttpContext.Response.Body.FlushAsync(ct);
        }
    }
}
