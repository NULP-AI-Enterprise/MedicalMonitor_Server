namespace PatientMonitoring.Api.Services.CentralStation;

public sealed class CentralStationOptions
{
    public const string SectionName = "CentralStation";

    /// <summary>
    /// Seconds without a reading after which a monitor counts as disconnected: its card is removed from the station
    /// (PatientRemoved, contract 5.2) and its last numbers are no longer sent as current.
    /// </summary>
    public int DeviceStaleAfterSeconds { get; set; } = 30;
}
