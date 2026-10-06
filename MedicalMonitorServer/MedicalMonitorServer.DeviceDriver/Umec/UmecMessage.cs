using MedicalMonitorServer.Contracts.Models;

namespace MedicalMonitorServer.DeviceDriver.Umec;

public sealed class UmecMessage
{
    public required DeviceState Device { get; init; }

    public required string Payload { get; init; }

    public PatientData? Patient { get; init; }
}
