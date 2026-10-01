using FastEndpoints;
using Microsoft.AspNetCore.Http;
using PatientMonitoring.Api.Contracts;
using PatientMonitoring.Api.Services.Parser;

namespace PatientMonitoring.Api.Endpoints.CentralStation;

/// <summary>
/// Ендпоінт прийому HL7 / ORU повідомлень монітора Mindray uMEC.
/// Використовує підключений IParserService з гілки parser.
/// </summary>
public sealed class IngestUmecHl7Endpoint(IUmecParserBridge parserBridge)
    : Endpoint<IngestUmecHl7Request, IngestUmecHl7Response>
{
    public override void Configure()
    {
        Post("/api/v1/ingestion/umec/hl7", "/api/vitals/ingest-hl7");
        AllowAnonymous();
        Summary(s =>
        {
            s.Summary = "Прийом сирих HL7 повідомлень Mindray uMEC";
            s.Description = "Парсить HL7 ORU/ADT пакет за допомогою ParserService та зберігає показники в систему.";
        });
    }

    public override async Task HandleAsync(IngestUmecHl7Request req, CancellationToken ct)
    {
        var rawMessage = req.RawMessage;

        if (string.IsNullOrWhiteSpace(rawMessage))
        {
            // Якщо передано як raw text/plain
            using var reader = new StreamReader(HttpContext.Request.Body);
            rawMessage = await reader.ReadToEndAsync(ct);
        }

        if (string.IsNullOrWhiteSpace(rawMessage))
        {
            await Send.ResultAsync(TypedResults.BadRequest(new { error = "Raw HL7 message is required." }));
            return;
        }

        var result = await parserBridge.IngestRawHl7Async(rawMessage, req.DeviceId, ct);

        if (!result.Success)
        {
            await Send.ResultAsync(TypedResults.BadRequest(result));
            return;
        }

        await Send.OkAsync(result, ct);
    }
}
