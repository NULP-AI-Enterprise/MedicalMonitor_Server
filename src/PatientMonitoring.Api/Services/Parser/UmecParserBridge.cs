using MedicalMonitorServer.Contracts.Models;
using MedicalMonitorServer.DeviceDriver.Umec;
using Microsoft.EntityFrameworkCore;
using PatientMonitoring.Api.Contracts;
using PatientMonitoring.Api.Data;
using PatientMonitoring.Api.Models;

namespace PatientMonitoring.Api.Services.Parser;

public sealed class UmecParserBridge(
    IParserService parserService,
    AppDbContext db,
    IVitalSignsService vitalsService,
    ILogger<UmecParserBridge> logger) : IUmecParserBridge
{
    public UmecMessage? ParseRawHl7(string rawHl7)
    {
        if (string.IsNullOrWhiteSpace(rawHl7)) return null;
        return parserService.Parse(rawHl7);
    }

    public UmecVitalsMessageDto MapToDto(UmecMessage message, string? defaultDeviceId = null)
    {
        var (hr, spo2, sys, dia, rr, temp) = ExtractVitals(message);
        var patientName = GetPatientName(message.Patient);
        var recordedAt = message.Vitals.FirstOrDefault(v => v.MeasuredAt.HasValue)?.MeasuredAt
            ?? (DateTimeOffset?)message.ReceivedAt;

        var alarms = message.Alarms.Select(a => $"{a.Text} ({a.Level})").ToList();

        return new UmecVitalsMessageDto
        {
            RawMessage = message.Raw,
            MessageType = message.MessageType,
            PatientId = message.Patient?.PatientId,
            PatientFullName = patientName,
            Ward = message.Patient?.Department,
            Bed = message.Patient?.BedNumber ?? message.Patient?.BedLabel,
            DeviceId = defaultDeviceId ?? message.Patient?.MonitorIp ?? message.ControlId,
            RecordedAt = recordedAt,
            HeartRate = hr,
            SystolicBloodPressure = sys,
            DiastolicBloodPressure = dia,
            OxygenSaturation = spo2,
            Temperature = temp,
            RespiratoryRate = rr,
            Alarms = alarms
        };
    }

    public async Task<IngestUmecHl7Response> IngestUmecMessageAsync(
        UmecMessage message,
        string? defaultDeviceId = null,
        CancellationToken ct = default)
    {
        var dto = MapToDto(message, defaultDeviceId);

        // Якщо повідомлення не містить жодного числового вітального показника
        var hasVitals = dto.HeartRate.HasValue
            || dto.SystolicBloodPressure.HasValue
            || dto.DiastolicBloodPressure.HasValue
            || dto.OxygenSaturation.HasValue
            || dto.Temperature.HasValue
            || dto.RespiratoryRate.HasValue;

        if (!hasVitals)
        {
            logger.LogInformation("Parsed uMEC message of kind {Kind} without measurable vitals", message.Kind);
            return new IngestUmecHl7Response(
                Success: true,
                MessageKind: message.Kind.ToString(),
                PatientId: null,
                PatientFullName: dto.PatientFullName,
                Vitals: null,
                Details: $"Message parsed as {message.Kind}, no numerical vitals to record.");
        }

        // Знайти або авто-створити пацієнта
        var patient = await ResolveOrCreatePatientAsync(dto, ct);

        var recordRequest = new RecordVitalSignsRequest
        {
            RecordedAt = dto.RecordedAt,
            DeviceId = dto.DeviceId,
            HeartRate = dto.HeartRate,
            SystolicBloodPressure = dto.SystolicBloodPressure,
            DiastolicBloodPressure = dto.DiastolicBloodPressure,
            OxygenSaturation = dto.OxygenSaturation,
            Temperature = dto.Temperature,
            RespiratoryRate = dto.RespiratoryRate
        };

        var recorded = await vitalsService.RecordAsync(patient.Id, recordRequest, ct);

        logger.LogInformation("Successfully ingested uMEC vitals for patient {PatientId} ({PatientName})",
            patient.Id, patient.FullName);

        return new IngestUmecHl7Response(
            Success: true,
            MessageKind: message.Kind.ToString(),
            PatientId: patient.Id,
            PatientFullName: patient.FullName,
            Vitals: recorded,
            Details: "Vitals successfully recorded and dispatched to real-time streams.");
    }

    public async Task<IngestUmecHl7Response> IngestRawHl7Async(
        string rawHl7,
        string? defaultDeviceId = null,
        CancellationToken ct = default)
    {
        var parsed = ParseRawHl7(rawHl7);
        if (parsed is null)
        {
            logger.LogWarning("Failed to parse HL7 message via uMEC ParserService");
            return new IngestUmecHl7Response(
                Success: false,
                MessageKind: "Unknown",
                PatientId: null,
                PatientFullName: null,
                Vitals: null,
                Details: "Invalid or unsupported HL7 payload.");
        }

        return await IngestUmecMessageAsync(parsed, defaultDeviceId, ct);
    }

    private static (int? hr, double? spo2, int? sys, int? dia, int? rr, double? temp) ExtractVitals(UmecMessage msg)
    {
        int? hr = null;
        double? spo2 = null;
        int? sys = null;
        int? dia = null;
        int? rr = null;
        double? temp = null;

        foreach (var v in msg.Vitals)
        {
            if (!v.Value.HasValue) continue;
            var val = v.Value.Value;

            switch (v.ParameterId)
            {
                case 101: // Heart Rate
                    hr = (int)Math.Round(val);
                    break;
                case 160: // SpO2
                    spo2 = Math.Round(val, 1);
                    break;
                case 170: // NIBP Systolic
                    sys = (int)Math.Round(val);
                    break;
                case 171: // NIBP Diastolic
                    dia = (int)Math.Round(val);
                    break;
                case 151: // Respiration Rate
                    rr = (int)Math.Round(val);
                    break;
                case 200 or 201 or 202: // Temp T1, T2, TD
                    temp = Math.Round(val, 1);
                    break;
                case 161 or 600: // PR (якщо немає HR)
                    hr ??= (int)Math.Round(val);
                    break;
            }
        }

        return (hr, spo2, sys, dia, rr, temp);
    }

    private static string? GetPatientName(MedicalMonitorServer.Contracts.Models.PatientInfo? p)
    {
        if (p is null) return null;
        if (!string.IsNullOrWhiteSpace(p.FullName)) return p.FullName.Trim();
        var combined = $"{p.LastName} {p.FirstName}".Trim();
        return string.IsNullOrWhiteSpace(combined) ? null : combined;
    }

    private async Task<Patient> ResolveOrCreatePatientAsync(UmecVitalsMessageDto dto, CancellationToken ct)
    {
        Patient? patient = null;

        // 1. Пошук за GUID PatientId
        if (!string.IsNullOrWhiteSpace(dto.PatientId) && Guid.TryParse(dto.PatientId, out var parsedGuid))
        {
            patient = await db.Patients.FirstOrDefaultAsync(p => p.Id == parsedGuid, ct);
        }

        // 2. Пошук за палатою та ліжком
        if (patient is null && !string.IsNullOrWhiteSpace(dto.Ward))
        {
            var wardClean = dto.Ward.Trim();
            var bedClean = dto.Bed?.Trim();

            var query = db.Patients.Where(p => p.IsActive && p.Ward == wardClean);
            if (!string.IsNullOrWhiteSpace(bedClean))
            {
                query = query.Where(p => p.Bed == bedClean);
            }
            patient = await query.FirstOrDefaultAsync(ct);
        }

        // 3. Пошук за ПІП
        if (patient is null && !string.IsNullOrWhiteSpace(dto.PatientFullName))
        {
            var nameClean = dto.PatientFullName.Trim().ToLower();
            patient = await db.Patients
                .FirstOrDefaultAsync(p => p.IsActive && p.FullName.ToLower() == nameClean, ct);
        }

        // 4. Авто-провіженінг нового пацієнта
        if (patient is null)
        {
            var autoName = !string.IsNullOrWhiteSpace(dto.PatientFullName)
                ? dto.PatientFullName.Trim()
                : $"uMEC Пацієнт ({dto.Ward ?? "Монітор"}{(string.IsNullOrWhiteSpace(dto.Bed) ? "" : $", л.{dto.Bed}")})";

            patient = new Patient
            {
                Id = Guid.NewGuid(),
                FullName = autoName,
                Ward = dto.Ward?.Trim(),
                Bed = dto.Bed?.Trim(),
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            };

            db.Patients.Add(patient);
            await db.SaveChangesAsync(ct);
        }

        return patient;
    }
}
