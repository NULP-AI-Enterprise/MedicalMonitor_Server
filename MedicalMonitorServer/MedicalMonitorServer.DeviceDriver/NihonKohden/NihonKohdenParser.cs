using System.Globalization;
using System.Text;
using MedicalMonitorServer.Contracts.Models;
using MedicalMonitorServer.DeviceDriver.Umec;

namespace MedicalMonitorServer.DeviceDriver.NihonKohden;

/// <summary>
/// Decodes the ORU^R01 a Nihon Kohden bedside monitor sends in HL7 mode (HL7 2.4, one message
/// a minute) into the same <see cref="UmecMessage"/> the uMec feed consumes. The HL7 plumbing
/// is <see cref="ParserService"/>'s; this adds what is Nihon Kohden's own: its six-digit
/// observation codes and its PV1-3.
/// <para>
/// Measured values are re-labelled with the Mindray parameter id of the same measurement
/// (see <see cref="VitalSign.ParameterId"/>), so everything after the driver handles both
/// makes alike. Observations with no Mindray counterpart (invasive pressures, ST of the
/// chest leads) stay in <see cref="UmecVitalsMessage.Observations"/> only.
/// </para>
/// <para>
/// Worked out from a live BSM-3562 (SW 07-41) in simulation mode; codes it did not send
/// (respiration, CO2, ...) are not known yet.
/// </para>
/// </summary>
public sealed class NihonKohdenParser : IParserService
{
    private readonly ParserService _hl7 = new();

    public UmecMessage? Parse(ReadOnlySpan<byte> message) => Parse(Encoding.Latin1.GetString(message));

    public UmecMessage? Parse(string message)
    {
        var parsed = _hl7.Parse(message);
        if (parsed?.Oru is not { } oru) return parsed;

        // Whatever the uMec rules made of the codes does not apply to this monitor.
        oru.Vitals.Clear();

        int temperatures = 0;
        foreach (var observation in oru.Observations)
        {
            if (observation.ValueType != "NM") continue;

            (int ParameterId, string Name) target;
            if (observation.Units == "C")
            {
                if (temperatures >= NihonKohdenCodes.Temperatures.Length) continue;
                target = NihonKohdenCodes.Temperatures[temperatures++];
            }
            else if (!NihonKohdenCodes.TryGetParameter(observation.Code, out target))
            {
                continue;
            }

            oru.Vitals.Add(new VitalSign
            {
                ParameterId = target.ParameterId,
                Name = target.Name,
                Label = observation.Label,
                Unit = observation.Units == "C" ? "°C" : observation.Units,
                Value = double.TryParse(observation.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                    ? value
                    : null,
                RawValue = observation.Value,
                IsAperiodic = NihonKohdenCodes.IsAperiodic(observation.Code),
                MeasuredAt = observation.Timestamp,
            });
        }

        oru.Patient = ToPatient(oru.Patient, message);

        // Every message of this stream is the minute's report, even when no sensor is attached
        // and it carries no value at all.
        parsed.Kind = UmecMessageKind.Vitals;
        return parsed;
    }

    /// <summary>
    /// PV1-3 is <c>^^&lt;bed name&gt;^&lt;monitor ip&gt;:&lt;n&gt;</c> here, not Mindray's
    /// ampersand list, so the fields read from it by the uMec rules are replaced.
    /// </summary>
    private static PatientInfo? ToPatient(PatientInfo? patient, string message)
    {
        if (patient is null) return null;

        var location = Field(message, "PV1", 3).Split('^');
        var bedName = location.Length > 2 ? location[2].Trim() : "";
        var address = location.Length > 3 ? location[3] : "";

        // PID is in every message; an empty PID-3 says the monitor has no patient id, which is
        // how the roster learns that a patient was discharged on the device.
        patient.Department = "";
        patient.BedLabel = bedName;
        patient.BedNumber = TrailingNumber(bedName);
        patient.MonitorIp = address.Split(':')[0];
        patient.MonitorPort = null;
        patient.Admitted = null;
        return patient;
    }

    /// <summary>"ICU-001" -> "1": the roster numbers beds, the monitor names them.</summary>
    private static string TrailingNumber(string bedName)
    {
        int start = bedName.Length;
        while (start > 0 && char.IsAsciiDigit(bedName[start - 1])) start--;

        var digits = bedName[start..].TrimStart('0');
        return digits.Length > 0 ? digits : "";
    }

    /// <summary>Field <paramref name="number"/> (HL7 numbering) of the first such segment; "" when absent.</summary>
    internal static string Field(string message, string segment, int number)
    {
        foreach (var line in message.Split('\r', '\n'))
        {
            var fields = line.Trim((char)0x0B, (char)0x1C, '\0').Split('|');
            if (fields[0] != segment) continue;

            // The '|' after "MSH" is itself MSH-1.
            int index = segment == "MSH" ? number - 1 : number;
            return index > 0 && index < fields.Length ? fields[index] : "";
        }

        return "";
    }
}
