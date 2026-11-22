using MedicalMonitorServer.Contracts.Api;
using MedicalMonitorServer.Contracts.Models;

namespace MedicalMonitorServer.Core.Monitoring;

/// <summary>
/// The measured values a station is sent as <see cref="ParameterDto"/>: everything a monitor
/// reports beyond the six vitals of <see cref="VitalsDto"/>. Drivers hand values over under
/// Mindray parameter ids (<see cref="VitalSign.ParameterId"/>); this is where an id becomes the
/// key, the name and the unit a station shows.
/// </summary>
public static class MonitorParameters
{
    /// <summary>Ids that are one of the six vitals, and so are not a parameter of their own.</summary>
    private static readonly HashSet<int> Vitals = new() { 101, 151, 160, 161, 600, 170, 171, 172, 200 };

    private static readonly Dictionary<int, (string Code, string Label, string Unit, string Group)> Known = new()
    {
        [102] = ("ecg.pvc", "PVCs", "/min", "ecg"),
        [105] = ("ecg.st.i", "ST-I", "mV", "ecg"),
        [106] = ("ecg.st.ii", "ST-II", "mV", "ecg"),
        [107] = ("ecg.st.iii", "ST-III", "mV", "ecg"),
        [108] = ("ecg.st.avr", "ST-aVR", "mV", "ecg"),
        [109] = ("ecg.st.avl", "ST-aVL", "mV", "ecg"),
        [110] = ("ecg.st.avf", "ST-aVF", "mV", "ecg"),
        [117] = ("ecg.st.v", "ST-V", "mV", "ecg"),
        [162] = ("spo2.pi", "PI", "%", "spo2"),
        [201] = ("temp.2", "T2", "°C", "temp"),
        [202] = ("temp.d", "TD", "°C", "temp"),
        [ServerParameterIds.ArtSystolic] = ("art.sys", "ART Sys", "mmHg", "art"),
        [ServerParameterIds.ArtDiastolic] = ("art.dia", "ART Dia", "mmHg", "art"),
        [ServerParameterIds.ArtMean] = ("art.mean", "ART Mean", "mmHg", "art"),
        [ServerParameterIds.CvpSystolic] = ("cvp.sys", "CVP Sys", "mmHg", "cvp"),
        [ServerParameterIds.CvpDiastolic] = ("cvp.dia", "CVP Dia", "mmHg", "cvp"),
        [ServerParameterIds.CvpMean] = ("cvp.mean", "CVP Mean", "mmHg", "cvp"),
        [ServerParameterIds.StV1] = ("ecg.st.v1", "ST-V1", "mV", "ecg"),
        [ServerParameterIds.StV1 + 1] = ("ecg.st.v2", "ST-V2", "mV", "ecg"),
        [ServerParameterIds.StV1 + 2] = ("ecg.st.v3", "ST-V3", "mV", "ecg"),
        [ServerParameterIds.StV1 + 3] = ("ecg.st.v4", "ST-V4", "mV", "ecg"),
        [ServerParameterIds.StV1 + 4] = ("ecg.st.v5", "ST-V5", "mV", "ecg"),
        [ServerParameterIds.StV1 + 5] = ("ecg.st.v6", "ST-V6", "mV", "ecg"),
    };

    /// <summary>
    /// The key a value of this parameter id is sent under; null for one of the six vitals and
    /// for a parameter without an id. A parameter the table does not know gets "p.{id}".
    /// </summary>
    public static string? CodeOf(int parameterId) =>
        parameterId <= 0 || Vitals.Contains(parameterId) ? null
        : Known.TryGetValue(parameterId, out var known) ? known.Code
        : $"p.{parameterId}";

    /// <summary>
    /// The parameter a station is sent for a measured value; null when the value is one of the
    /// six vitals, has no number, or is unknown here and comes without a name to show it under.
    /// </summary>
    public static ParameterDto? Describe(VitalSign vital)
    {
        if (vital.Value is not double value || CodeOf(vital.ParameterId) is not { } code) return null;

        if (Known.TryGetValue(vital.ParameterId, out var known))
        {
            return new ParameterDto(code, known.Label, value, known.Unit, known.Group);
        }

        // Not in the table: shown under what the monitor itself calls it, or not at all - a
        // number without a name is of no use at a bedside, and can be mistaken for another.
        var label = !string.IsNullOrWhiteSpace(vital.Name) ? vital.Name : vital.Label;
        return string.IsNullOrWhiteSpace(label) ? null : new ParameterDto(code, label.Trim(), value, vital.Unit, "other");
    }
}
