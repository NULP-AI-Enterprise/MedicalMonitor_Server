using System.Text.Json.Serialization;

namespace PatientMonitoring.Api.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VitalStatus
{
    Normal = 0,
    Warning = 1,
    Critical = 2
}
