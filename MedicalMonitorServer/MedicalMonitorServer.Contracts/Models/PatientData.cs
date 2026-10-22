namespace MedicalMonitorServer.Contracts.Models;

public sealed class PatientData
{
    public string PatientId { get; init; } = string.Empty;

    public string? Name { get; init; }

    public IReadOnlyDictionary<string, string> Values { get; init; } =
        new Dictionary<string, string>();
}
