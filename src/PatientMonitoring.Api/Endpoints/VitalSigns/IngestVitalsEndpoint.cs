using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using PatientMonitoring.Api.Contracts;
using PatientMonitoring.Api.Data;
using PatientMonitoring.Api.Models;
using PatientMonitoring.Api.Services;

namespace PatientMonitoring.Api.Endpoints.VitalSigns;

public sealed class IngestVitalsEndpoint(
    AppDbContext db,
    IVitalSignsService vitalsService,
    ILogger<IngestVitalsEndpoint> logger)
    : Endpoint<IngestVitalSignsRequest, VitalSignsResponse>
{
    public override void Configure()
    {
        Post("/api/vitals/ingest");
        AllowAnonymous();
    }

    public override async Task HandleAsync(IngestVitalSignsRequest req, CancellationToken ct)
    {
        Patient? patient = null;

        // 1. Пошук за прямим PatientId
        if (req.PatientId is { } pid)
        {
            patient = await db.Patients.FirstOrDefaultAsync(p => p.Id == pid, ct);
        }

        // 2. Пошук за палатою та ліжком (для моніторів у палатах)
        if (patient is null && !string.IsNullOrWhiteSpace(req.Ward))
        {
            var wardClean = req.Ward.Trim();
            var bedClean = req.Bed?.Trim();

            var query = db.Patients.Where(p => p.IsActive && p.Ward == wardClean);
            if (!string.IsNullOrWhiteSpace(bedClean))
            {
                query = query.Where(p => p.Bed == bedClean);
            }

            patient = await query.FirstOrDefaultAsync(ct);
        }

        // 3. Пошук за ПІП пацієнта
        if (patient is null && !string.IsNullOrWhiteSpace(req.PatientFullName))
        {
            var nameClean = req.PatientFullName.Trim().ToLower();
            patient = await db.Patients
                .FirstOrDefaultAsync(p => p.IsActive && p.FullName.ToLower() == nameClean, ct);
        }

        // 4. Якщо пацієнта не знайдено — авто-створюємо картку (щоб дані парсера не губились)
        if (patient is null)
        {
            var autoName = !string.IsNullOrWhiteSpace(req.PatientFullName)
                ? req.PatientFullName.Trim()
                : $"Пацієнт ({req.Ward ?? "Сенсор"}{(string.IsNullOrWhiteSpace(req.Bed) ? "" : $", л.{req.Bed}")})";

            patient = new Patient
            {
                Id = Guid.NewGuid(),
                FullName = autoName,
                Ward = req.Ward?.Trim(),
                Bed = req.Bed?.Trim(),
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            };

            db.Patients.Add(patient);
            await db.SaveChangesAsync(ct);

            logger.LogInformation("Auto-provisioned patient {PatientId} ({FullName}) for incoming parser data", patient.Id, patient.FullName);
        }

        var recordRequest = new RecordVitalSignsRequest
        {
            RecordedAt = req.RecordedAt,
            DeviceId = req.DeviceId,
            HeartRate = req.HeartRate,
            SystolicBloodPressure = req.SystolicBloodPressure,
            DiastolicBloodPressure = req.DiastolicBloodPressure,
            OxygenSaturation = req.OxygenSaturation,
            Temperature = req.Temperature,
            RespiratoryRate = req.RespiratoryRate
        };

        var result = await vitalsService.RecordAsync(patient.Id, recordRequest, ct);
        if (result is null)
        {
            await Send.ResultAsync(TypedResults.Json(new { error = "Failed to record vital signs." }, statusCode: StatusCodes.Status400BadRequest));
            return;
        }

        await Send.CreatedAtAsync<GetLatestPatientVitalsEndpoint>(
            routeValues: new { patientId = patient.Id },
            responseBody: result,
            cancellation: ct);
    }
}
