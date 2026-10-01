using Microsoft.EntityFrameworkCore;
using PatientMonitoring.Api.Contracts;
using PatientMonitoring.Api.Data;
using PatientMonitoring.Api.Models;
using PatientMonitoring.Api.Services.Alerting;
using PatientMonitoring.Api.Services.Caching;
using PatientMonitoring.Api.Services.Relay;

using PatientMonitoring.Api.Services.CentralStation;

namespace PatientMonitoring.Api.Services;

public interface IVitalSignsService
{
    /// <summary>
    /// Зберігає нові показники, оцінює стан і розсилає їх підписаним клієнтам.
    /// Повертає null, якщо пацієнта не існує.
    /// </summary>
    Task<VitalSignsResponse?> RecordAsync(Guid patientId, RecordVitalSignsRequest request, CancellationToken cancellationToken = default);

    Task<VitalSignsResponse?> GetLatestAsync(Guid patientId, CancellationToken cancellationToken = default);

    /// <summary>Пакетне збереження показників (наприклад, для пристроїв після втрати зв'язку).</summary>
    Task<RecordVitalSignsBatchResponse?> RecordBatchAsync(
        Guid patientId,
        RecordVitalSignsBatchRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Історія показників, від найновіших до найстаріших.</summary>
    Task<IReadOnlyList<VitalSignsResponse>> GetHistoryAsync(
        Guid patientId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int limit,
        CancellationToken cancellationToken = default);
}

public sealed class VitalSignsService(
    AppDbContext db,
    IVitalSignsAnalyzer analyzer,
    IPatientNotifier notifier,
    IVitalSignsCache cache,
    IAlertDispatcher alertDispatcher,
    IRealtimeDataRelay dataRelay,
    ISseStreamService sseStream,
    ILogger<VitalSignsService> logger,
    ICentralStationService? centralStation = null) : IVitalSignsService
{
    public const int MaxHistoryLimit = 1000;

    public async Task<VitalSignsResponse?> RecordAsync(
        Guid patientId,
        RecordVitalSignsRequest request,
        CancellationToken cancellationToken = default)
    {
        var patient = await db.Patients
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == patientId, cancellationToken);

        if (patient is null)
        {
            return null;
        }

        var now = DateTime.UtcNow;
        var vitals = new VitalSign
        {
            PatientId = patientId,
            RecordedAt = request.RecordedAt?.UtcDateTime ?? now,
            ReceivedAt = now,
            DeviceId = request.DeviceId?.Trim(),
            HeartRate = request.HeartRate,
            SystolicBloodPressure = request.SystolicBloodPressure,
            DiastolicBloodPressure = request.DiastolicBloodPressure,
            OxygenSaturation = request.OxygenSaturation,
            Temperature = request.Temperature,
            RespiratoryRate = request.RespiratoryRate
        };

        var analysis = analyzer.Analyze(vitals);
        vitals.Status = analysis.Status;

        db.VitalSigns.Add(vitals);
        await db.SaveChangesAsync(cancellationToken);

        var response = vitals.ToResponse(analysis.Alerts, patient.FullName);

        // Оновлюємо кеш останніх показників
        await cache.SetLatestAsync(patientId, response, CancellationToken.None);

        // Дані вже збережено — розсилку не скасовуємо, навіть якщо клієнт обірвав HTTP-запит.
        await notifier.NotifyVitalSignsAsync(response, CancellationToken.None);
        sseStream.Broadcast(response);
        _ = dataRelay.RelayVitalSignsAsync(response, CancellationToken.None);
        _ = centralStation?.ProcessVitalSignsRecordedAsync(patient, vitals, CancellationToken.None);

        if (analysis.Status != VitalStatus.Normal)
        {
            logger.LogWarning(
                "Patient {PatientId} ({PatientName}) status is {Status}: {Alerts}",
                patient.Id, patient.FullName, analysis.Status, string.Join("; ", analysis.Alerts.Select(a => a.Message)));

            var notification = new PatientAlertNotification(patient.Id, patient.FullName, analysis.Status, vitals.RecordedAt, analysis.Alerts);
            await notifier.NotifyAlertAsync(notification, CancellationToken.None);
            await alertDispatcher.DispatchAsync(notification, CancellationToken.None);
        }

        return response;
    }

    public async Task<VitalSignsResponse?> GetLatestAsync(Guid patientId, CancellationToken cancellationToken = default)
    {
        var cached = await cache.GetLatestAsync(patientId, cancellationToken);
        if (cached is not null)
        {
            return cached;
        }

        var latest = await db.VitalSigns
            .Include(v => v.Patient)
            .AsNoTracking()
            .Where(v => v.PatientId == patientId)
            .OrderByDescending(v => v.RecordedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (latest is null)
        {
            return null;
        }

        var response = latest.ToResponse(analyzer);
        await cache.SetLatestAsync(patientId, response, cancellationToken);
        return response;
    }

    public async Task<IReadOnlyList<VitalSignsResponse>> GetHistoryAsync(
        Guid patientId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var query = db.VitalSigns
            .Include(v => v.Patient)
            .AsNoTracking()
            .Where(v => v.PatientId == patientId);

        if (from is { } fromValue)
        {
            var fromUtc = fromValue.UtcDateTime;
            query = query.Where(v => v.RecordedAt >= fromUtc);
        }

        if (to is { } toValue)
        {
            var toUtc = toValue.UtcDateTime;
            query = query.Where(v => v.RecordedAt <= toUtc);
        }

        var rows = await query
            .OrderByDescending(v => v.RecordedAt)
            .Take(Math.Clamp(limit, 1, MaxHistoryLimit))
            .ToListAsync(cancellationToken);

        return rows.Select(v => v.ToResponse(analyzer)).ToList();
    }

    public async Task<RecordVitalSignsBatchResponse?> RecordBatchAsync(
        Guid patientId,
        RecordVitalSignsBatchRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Items.Count == 0)
        {
            return new RecordVitalSignsBatchResponse(patientId, 0, null);
        }

        var patient = await db.Patients
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == patientId, cancellationToken);

        if (patient is null)
        {
            return null;
        }

        var now = DateTime.UtcNow;
        var entities = new List<VitalSign>(request.Items.Count);

        foreach (var item in request.Items)
        {
            var vitals = new VitalSign
            {
                PatientId = patientId,
                RecordedAt = item.RecordedAt?.UtcDateTime ?? now,
                ReceivedAt = now,
                DeviceId = item.DeviceId?.Trim(),
                HeartRate = item.HeartRate,
                SystolicBloodPressure = item.SystolicBloodPressure,
                DiastolicBloodPressure = item.DiastolicBloodPressure,
                OxygenSaturation = item.OxygenSaturation,
                Temperature = item.Temperature,
                RespiratoryRate = item.RespiratoryRate
            };

            var analysis = analyzer.Analyze(vitals);
            vitals.Status = analysis.Status;
            entities.Add(vitals);
        }

        db.VitalSigns.AddRange(entities);
        await db.SaveChangesAsync(cancellationToken);

        var latestEntity = entities.OrderByDescending(e => e.RecordedAt).First();
        var latestAnalysis = analyzer.Analyze(latestEntity);
        var latestResponse = latestEntity.ToResponse(latestAnalysis.Alerts, patient.FullName);

        await cache.SetLatestAsync(patientId, latestResponse, CancellationToken.None);
        await notifier.NotifyVitalSignsAsync(latestResponse, CancellationToken.None);
        sseStream.Broadcast(latestResponse);

        if (latestAnalysis.Status != VitalStatus.Normal)
        {
            logger.LogWarning(
                "Patient {PatientId} ({PatientName}) batch latest status is {Status}: {Alerts}",
                patient.Id, patient.FullName, latestAnalysis.Status,
                string.Join("; ", latestAnalysis.Alerts.Select(a => a.Message)));

            var notification = new PatientAlertNotification(patient.Id, patient.FullName, latestAnalysis.Status, latestEntity.RecordedAt, latestAnalysis.Alerts);
            await notifier.NotifyAlertAsync(notification, CancellationToken.None);
            await alertDispatcher.DispatchAsync(notification, CancellationToken.None);
        }

        var batchResponse = new RecordVitalSignsBatchResponse(patientId, entities.Count, latestResponse);
        _ = dataRelay.RelayBatchAsync(batchResponse, CancellationToken.None);
        return batchResponse;
    }
}
