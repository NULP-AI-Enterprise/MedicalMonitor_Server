using System.Text;
using FastEndpoints;
using PatientMonitoring.Api.Services;

namespace PatientMonitoring.Api.Endpoints.VitalSigns;

public sealed class ExportPatientVitalsEndpoint(IVitalSignsService vitals)
    : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/api/patients/{patientId:guid}/vitals/export");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var patientId = Route<Guid>("patientId");
        var format = Query<string>("format", isRequired: false) ?? "csv";
        var from = Query<DateTimeOffset?>("from", isRequired: false);
        var to = Query<DateTimeOffset?>("to", isRequired: false);
        var limit = Query<int>("limit", isRequired: false);
        if (limit <= 0) limit = 1000;

        var history = await vitals.GetHistoryAsync(patientId, from, to, limit, ct);

        if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
        {
            await Send.OkAsync(history, ct);
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("Id,PatientId,PatientFullName,RecordedAt,DeviceId,HeartRate,SystolicBloodPressure,DiastolicBloodPressure,OxygenSaturation,Temperature,RespiratoryRate,Status");
        foreach (var item in history)
        {
            sb.AppendLine(string.Join(",",
                item.Id,
                item.PatientId,
                $"\"{item.PatientFullName?.Replace("\"", "\"\"")}\"",
                item.RecordedAt.ToString("o"),
                $"\"{item.DeviceId?.Replace("\"", "\"\"")}\"",
                item.HeartRate?.ToString() ?? "",
                item.SystolicBloodPressure?.ToString() ?? "",
                item.DiastolicBloodPressure?.ToString() ?? "",
                item.OxygenSaturation?.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) ?? "",
                item.Temperature?.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) ?? "",
                item.RespiratoryRate?.ToString() ?? "",
                item.Status
            ));
        }

        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
        await Send.BytesAsync(bytes, $"vitals_{patientId}_{DateTime.UtcNow:yyyyMMddHHmmss}.csv", "text/csv", cancellation: ct);
    }
}
