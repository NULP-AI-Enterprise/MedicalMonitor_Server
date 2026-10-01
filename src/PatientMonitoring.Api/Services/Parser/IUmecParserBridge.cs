using MedicalMonitorServer.Contracts.Models;
using PatientMonitoring.Api.Contracts;

namespace PatientMonitoring.Api.Services.Parser;

public interface IUmecParserBridge
{
    /// <summary>
    /// Парсить сире HL7/ORU/ADT повідомлення через IParserService з гілки parser.
    /// </summary>
    UmecMessage? ParseRawHl7(string rawHl7);

    /// <summary>
    /// Конвертує розпарсений UmecMessage у стандартизований UmecVitalsMessageDto.
    /// </summary>
    UmecVitalsMessageDto MapToDto(UmecMessage message, string? defaultDeviceId = null);

    /// <summary>
    /// Приймає розпарсений UmecMessage, знаходить/створює картку пацієнта та зберігає показники в БД і SignalR.
    /// </summary>
    Task<IngestUmecHl7Response> IngestUmecMessageAsync(
        UmecMessage message,
        string? defaultDeviceId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Наскрізний конвеєр: парсинг сирого HL7 тексту та збереження вітальних показників.
    /// </summary>
    Task<IngestUmecHl7Response> IngestRawHl7Async(
        string rawHl7,
        string? defaultDeviceId = null,
        CancellationToken ct = default);
}
