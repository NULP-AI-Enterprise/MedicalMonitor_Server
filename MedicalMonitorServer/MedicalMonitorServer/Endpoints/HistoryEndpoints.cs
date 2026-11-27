using System.Text.Json;
using FastEndpoints;
using MedicalMonitorServer.Contracts.Api;
using MedicalMonitorServer.Dal.History;
using MedicalMonitorServer.Services;

namespace MedicalMonitorServer.Endpoints;

// The recorded history of admitted patients. It names every patient who was ever admitted
// here, so unlike the other reads it is not open to the ward network: ApiKeyMiddleware asks
// for the operator key, or the request must come from the station on this machine.

/// <summary>Query of the list of recorded patients.</summary>
public sealed class HistoryPatientsRequest
{
    /// <summary>Only the admissions with this DrAlex id; all of them when left out.</summary>
    public string? DrAlexId { get; set; }

    /// <summary>
    /// Only the patients who are discharged now, the last discharged first: who can be admitted
    /// again. Left out, everybody is listed, the last admitted first.
    /// </summary>
    public bool Discharged { get; set; }

    /// <summary>How many at most; 100 when left out, 1000 at most.</summary>
    public int Limit { get; set; }
}

/// <summary>Patient id from the route and the stretch of time asked for.</summary>
public sealed class HistoryVitalsRequest
{
    public string PatientId { get; set; } = string.Empty;

    /// <summary>First second wanted; the patient's first admission when left out.</summary>
    public DateTimeOffset? From { get; set; }

    /// <summary>Last second wanted; now when left out.</summary>
    public DateTimeOffset? To { get; set; }

    /// <summary>How many seconds at most, the newest of the stretch; 600 when left out, 86 400 (a day) at most.</summary>
    public int Limit { get; set; }
}

public sealed class GetHistoryPatientsEndpoint : Endpoint<HistoryPatientsRequest, List<RecordedPatientDto>>
{
    private const int DefaultLimit = 100;
    private const int MaxLimit = 1000;

    private readonly PatientHistory _history;

    public GetHistoryPatientsEndpoint(PatientHistory history) => _history = history;

    public override void Configure()
    {
        Get("/api/v1/history/patients");
        AllowAnonymous();
        Summary(s =>
        {
            s.Summary = "Patients whose values were recorded, the last admitted first";
            s.Description =
                "Everybody a station admitted, on a monitor now or discharged since, each with their stays " +
                "(admissions). ?drAlexId= narrows it to the patients with one DrAlex id, ?limit= to so many (100 by " +
                "default). ?discharged=true lists only those who are discharged now, the last discharged first: the " +
                "patients a station can admit again (POST /api/v1/patients/{id}/admit with existingPatientId). " +
                "Needs the operator key unless asked from the station on the server's own computer. 503 when the " +
                "server has no history database.";
        });
    }

    public override async Task HandleAsync(HistoryPatientsRequest req, CancellationToken ct)
    {
        if (!_history.IsOn)
        {
            AddError(PatientHistoryErrors.Unavailable);
            await Send.ErrorsAsync(StatusCodes.Status503ServiceUnavailable, ct);
            return;
        }

        var limit = req.Limit <= 0 ? DefaultLimit : Math.Min(req.Limit, MaxLimit);
        var drAlexId = string.IsNullOrWhiteSpace(req.DrAlexId) ? null : req.DrAlexId.Trim();

        List<RecordedPatientDto> patients;
        try
        {
            var recorded = req.Discharged ? _history.GetDischarged(drAlexId, limit) : _history.GetPatients(drAlexId, limit);
            var stays = _history.GetAdmissions(recorded.Select(p => p.Num).ToList());
            patients = recorded.Select(p => HistoryMapping.ToDto(p, stays.GetValueOrDefault(p.Num))).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogError(ex, "The history database could not be read.");
            AddError(PatientHistoryErrors.Unreadable);
            await Send.ErrorsAsync(StatusCodes.Status503ServiceUnavailable, ct);
            return;
        }

        await Send.OkAsync(patients, ct);
    }
}

public sealed class GetHistoryPatientEndpoint : Endpoint<PatientRouteRequest, RecordedPatientDto>
{
    private readonly PatientHistory _history;

    public GetHistoryPatientEndpoint(PatientHistory history) => _history = history;

    public override void Configure()
    {
        Get("/api/v1/history/patients/{patientId}");
        AllowAnonymous();
        Summary(s =>
        {
            s.Summary = "One recorded patient, with every stay of theirs";
            s.Description =
                "Who a patient id of the history belongs to: what a station shows before it admits them again. " +
                "Needs the operator key unless asked from the station on the server's own computer. 404 for an id " +
                "nobody was admitted under, 503 when the server has no history database.";
        });
    }

    public override async Task HandleAsync(PatientRouteRequest req, CancellationToken ct)
    {
        if (!_history.IsOn)
        {
            AddError(PatientHistoryErrors.Unavailable);
            await Send.ErrorsAsync(StatusCodes.Status503ServiceUnavailable, ct);
            return;
        }

        RecordedPatientDto? patient = null;
        try
        {
            // As pasted at a station: in capitals, with blanks around it.
            if (PatientValidation.RecordedPatientId(req.PatientId) is { } id && _history.GetPatient(id) is { } recorded)
            {
                patient = HistoryMapping.ToDto(recorded, _history.GetAdmissions(new[] { recorded.Num }).GetValueOrDefault(recorded.Num));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogError(ex, "The history database could not be read.");
            AddError(PatientHistoryErrors.Unreadable);
            await Send.ErrorsAsync(StatusCodes.Status503ServiceUnavailable, ct);
            return;
        }

        if (patient is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(patient, ct);
    }
}

public sealed class GetHistoryVitalsEndpoint : Endpoint<HistoryVitalsRequest, List<VitalsRecordDto>>
{
    private const int DefaultLimit = 600;
    private const int MaxLimit = 86_400;

    private readonly PatientHistory _history;

    public GetHistoryVitalsEndpoint(PatientHistory history) => _history = history;

    public override void Configure()
    {
        Get("/api/v1/history/patients/{patientId}/vitals");
        AllowAnonymous();
        Summary(s =>
        {
            s.Summary = "The recorded values of one patient, a record per second, oldest first";
            s.Description =
                "The newest ?limit= seconds (600 by default, a day at most) between ?from= and ?to= (ISO 8601; " +
                "the patient's first admission and now when left out). A monitor that said nothing for more than " +
                "three seconds leaves a gap: there are no records for that time. Nor are there any between two " +
                "stays of a patient who was discharged and admitted again. " +
                "Needs the operator key unless asked from the station on the server's own computer. 404 for " +
                "somebody who was never admitted, 503 when the server has no history database.";
        });
    }

    public override async Task HandleAsync(HistoryVitalsRequest req, CancellationToken ct)
    {
        if (!_history.IsOn)
        {
            AddError(PatientHistoryErrors.Unavailable);
            await Send.ErrorsAsync(StatusCodes.Status503ServiceUnavailable, ct);
            return;
        }

        var limit = req.Limit <= 0 ? DefaultLimit : Math.Min(req.Limit, MaxLimit);

        List<VitalsRecordDto>? records = null;
        try
        {
            if (PatientValidation.RecordedPatientId(req.PatientId) is { } id && _history.GetPatient(id) is { } patient)
            {
                // From the first admission: nothing was recorded before it, so the beginning of
                // time is the same, whichever stay the patient is in.
                var from = req.From?.ToUnixTimeSeconds() ?? 0;
                var to = (req.To ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
                records = _history.GetVitals(patient.Num, from, to, limit).Select(HistoryMapping.ToDto).ToList();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogError(ex, "The history database could not be read.");
            AddError(PatientHistoryErrors.Unreadable);
            await Send.ErrorsAsync(StatusCodes.Status503ServiceUnavailable, ct);
            return;
        }

        if (records is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(records, ct);
    }
}

internal static class HistoryMapping
{
    /// <param name="stays">The patient's stays in the order they happened; null when the database has none for them.</param>
    public static RecordedPatientDto ToDto(RecordedPatient patient, IReadOnlyList<RecordedAdmission>? stays) => new(
        patient.Id,
        patient.DrAlexId,
        patient.FullName ?? string.Empty,
        patient.BedNumber,
        patient.PatientType,
        patient.Diagnosis,
        patient.MonitorIp,
        patient.AdmittedAt,
        patient.DischargedAt,
        patient.LastRecordedAt is { } last ? DateTimeOffset.FromUnixTimeSeconds(last) : null,
        (stays ?? Array.Empty<RecordedAdmission>())
            .Select(stay => new AdmissionDto(stay.AdmittedAt, stay.DischargedAt, stay.BedNumber, stay.MonitorIp))
            .ToList());

    public static VitalsRecordDto ToDto(VitalsRow row) => new(
        DateTimeOffset.FromUnixTimeSeconds(row.Timestamp),
        row.HeartRate,
        row.SpO2,
        row.PulseRate,
        row.RespiratoryRate,
        row.Temperature,
        row.Cuff is { } cuff
            ? new BloodPressureDto(
                cuff.Systolic,
                cuff.Diastolic,
                cuff.Mean,
                cuff.MeasuredAt is { } at ? DateTimeOffset.FromUnixTimeSeconds(at) : null)
            : null,
        Parameters(row.Extra));

    private static IReadOnlyDictionary<string, double>? Parameters(string? extra)
    {
        if (string.IsNullOrEmpty(extra)) return null;

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, double>>(extra);
        }
        catch (JsonException)
        {
            // A row somebody edited by hand must not take the whole answer down with it.
            return null;
        }
    }
}
