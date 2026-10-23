using MedicalMonitorServer.Contracts.Models;
using PatientMonitoring.Api.Contracts;
using PatientMonitoring.Api.Services.CentralStation;
using PatientMonitoring.Api.Services.Parser;

namespace PatientMonitoring.Api.Services;

/// <summary>
/// Фасадний сервіс API (згідно з архітектурною діаграмою сервісу),
/// що консолідує роботу з вітальними показниками, парсером uMEC та станцією моніторингу.
/// </summary>
public interface IApiService
{
    IVitalSignsService Vitals { get; }
    IPatientService Patients { get; }
    IUmecParserBridge UmecParser { get; }
    ICentralStationService CentralStation { get; }

    /// <summary>
    /// Пряма обробка повідомлення UmecVitalsMessageDto або UmecMessage.
    /// </summary>
    Task<IngestUmecHl7Response> ProcessUmecMessageAsync(UmecMessage message, string? deviceId = null, CancellationToken ct = default);

    Task<IngestUmecHl7Response> ProcessRawHl7Async(string rawHl7, string? deviceId = null, CancellationToken ct = default);
}

public sealed class ApiService(
    IVitalSignsService vitals,
    IPatientService patients,
    IUmecParserBridge umecParser,
    ICentralStationService centralStation) : IApiService
{
    public IVitalSignsService Vitals => vitals;
    public IPatientService Patients => patients;
    public IUmecParserBridge UmecParser => umecParser;
    public ICentralStationService CentralStation => centralStation;

    public Task<IngestUmecHl7Response> ProcessUmecMessageAsync(
        UmecMessage message,
        string? deviceId = null,
        CancellationToken ct = default)
    {
        return umecParser.IngestUmecMessageAsync(message, deviceId, ct);
    }

    public Task<IngestUmecHl7Response> ProcessRawHl7Async(
        string rawHl7,
        string? deviceId = null,
        CancellationToken ct = default)
    {
        return umecParser.IngestRawHl7Async(rawHl7, deviceId, ct);
    }
}
