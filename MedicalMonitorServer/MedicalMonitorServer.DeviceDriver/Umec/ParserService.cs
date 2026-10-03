using MedicalMonitorServer.Contracts.Models;

namespace MedicalMonitorServer.DeviceDriver.Umec;

public class ParserService
{
    public UmecHl7Message Parse(string raw, DateTimeOffset? receivedAt = null)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new ArgumentException("HL7 payload cannot be empty.", nameof(raw));
        }

        var segments = raw
            .Replace("\n", "\r", StringComparison.Ordinal)
            .Split('\r', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var msh = FindSegment(segments, "MSH");
        var messageType = GetMshField(msh, 9);
        var controlId = GetMshField(msh, 10);
        var hl7Version = GetMshField(msh, 12);
        var parsedAt = receivedAt ?? DateTimeOffset.UtcNow;

        if (string.Equals(messageType, "ORU^R01", StringComparison.OrdinalIgnoreCase))
        {
            return ParseOruMessage(segments, messageType, controlId, hl7Version, parsedAt, raw);
        }

        if (string.Equals(messageType, "ADT^A01", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(messageType, "ATD^A01", StringComparison.OrdinalIgnoreCase))
        {
            return ParseAdtMessage(segments, messageType, controlId, hl7Version, parsedAt, raw);
        }

        throw new NotSupportedException($"Unsupported HL7 message type '{messageType ?? "unknown"}'.");
    }

    public UmecOruMessage ParseOru(string raw, DateTimeOffset? receivedAt = null)
    {
        var parsed = Parse(raw, receivedAt);
        return parsed as UmecOruMessage
            ?? throw new InvalidOperationException("Payload is not an ORU^R01 message.");
    }

    public UmecAdtMessage ParseAdt(string raw, DateTimeOffset? receivedAt = null)
    {
        var parsed = Parse(raw, receivedAt);
        return parsed as UmecAdtMessage
            ?? throw new InvalidOperationException("Payload is not an ADT^A01 message.");
    }

    private static UmecOruMessage ParseOruMessage(
        IReadOnlyCollection<string> segments,
        string? messageType,
        string? controlId,
        string? hl7Version,
        DateTimeOffset receivedAt,
        string raw)
    {
        var observations = new List<UmecObservation>();
        var vitals = new List<VitalSign>();
        var labels = new Dictionary<string, ParameterLabel>(StringComparer.OrdinalIgnoreCase);
        var groupLookup = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var alarmSettings = new List<AlarmSetting>();
        var activeAlarms = new List<ActiveAlarm>();
        AlarmSystemStatus? alarmSystemStatus = null;

        foreach (var segment in segments)
        {
            if (segment.StartsWith("OBX|", StringComparison.OrdinalIgnoreCase))
            {
                var idField = GetField(segment, 3);
                var value = GetField(segment, 5);
                var units = GetField(segment, 6);
                var status = GetField(segment, 11);
                var idParts = SplitComponents(idField);

                var code = idParts.ElementAtOrDefault(0);
                var name = idParts.ElementAtOrDefault(1);
                var group = idParts.ElementAtOrDefault(2);

                var observation = new UmecObservation
                {
                    Code = code,
                    Name = name,
                    Group = group,
                    Value = value,
                    Units = units,
                    Status = status
                };

                observations.Add(observation);

                if (!string.IsNullOrWhiteSpace(code) && !labels.ContainsKey(code))
                {
                    labels[code] = new ParameterLabel
                    {
                        Code = code,
                        Label = name,
                        Group = group
                    };
                }

                if (!string.IsNullOrWhiteSpace(code) && !string.IsNullOrWhiteSpace(group))
                {
                    if (!groupLookup.TryGetValue(group, out var groupParameters))
                    {
                        groupParameters = [];
                        groupLookup[group] = groupParameters;
                    }

                    groupParameters.Add(code);
                }

                if (double.TryParse(value, out var numericValue))
                {
                    vitals.Add(new VitalSign
                    {
                        Code = code,
                        Name = name,
                        Value = numericValue,
                        Units = units
                    });
                }
            }
            else if (segment.StartsWith("ALM|", StringComparison.OrdinalIgnoreCase))
            {
                activeAlarms.Add(new ActiveAlarm
                {
                    Code = GetField(segment, 1),
                    Severity = GetField(segment, 2),
                    Description = GetField(segment, 3)
                });
            }
            else if (segment.StartsWith("ZAS|", StringComparison.OrdinalIgnoreCase))
            {
                alarmSettings.Add(new AlarmSetting
                {
                    Code = GetField(segment, 1),
                    Value = GetField(segment, 2)
                });
            }
            else if (segment.StartsWith("ZST|", StringComparison.OrdinalIgnoreCase))
            {
                alarmSystemStatus = new AlarmSystemStatus
                {
                    Status = GetField(segment, 1),
                    Details = GetField(segment, 2)
                };
            }
        }

        var parameterGroups = groupLookup
            .Select(group => new ParameterGroup
            {
                Name = group.Key,
                ParameterCodes = group.Value.ToList()
            })
            .ToList();

        return new UmecOruMessage
        {
            MessageType = messageType,
            ControlId = controlId,
            Hl7Version = hl7Version,
            ReceivedAt = receivedAt,
            Raw = raw,
            Patient = ParsePatientInfo(segments),
            VitalSigns = vitals,
            AlarmSettings = alarmSettings,
            AlarmSystemStatus = alarmSystemStatus,
            ActiveAlarms = activeAlarms,
            ParameterLabels = labels.Values.ToList(),
            ParameterGroups = parameterGroups,
            Observations = observations
        };
    }

    private static UmecAdtMessage ParseAdtMessage(
        IReadOnlyCollection<string> segments,
        string? messageType,
        string? controlId,
        string? hl7Version,
        DateTimeOffset receivedAt,
        string raw)
    {
        var msh = FindSegment(segments, "MSH");
        var evn = segments.FirstOrDefault(x => x.StartsWith("EVN|", StringComparison.OrdinalIgnoreCase));
        var pv1 = segments.FirstOrDefault(x => x.StartsWith("PV1|", StringComparison.OrdinalIgnoreCase));

        return new UmecAdtMessage
        {
            MessageType = messageType,
            ControlId = controlId,
            Hl7Version = hl7Version,
            ReceivedAt = receivedAt,
            Raw = raw,
            Monitor = new MonitorInfo
            {
                DeviceId = GetMshField(msh, 3),
                Model = GetMshField(msh, 4),
                Status = GetField(pv1, 2),
                Location = GetField(pv1, 3)
            },
            Patient = ParsePatientInfo(segments),
            EventCode = GetField(evn, 1) ?? messageType,
            EventTimestamp = ParseHl7DateTime(GetField(evn, 2))
        };
    }

    private static PatientInfo? ParsePatientInfo(IEnumerable<string> segments)
    {
        var pid = segments.FirstOrDefault(x => x.StartsWith("PID|", StringComparison.OrdinalIgnoreCase));
        if (pid is null)
        {
            return null;
        }

        var nameParts = SplitComponents(GetField(pid, 5));

        return new PatientInfo
        {
            Id = GetField(pid, 3),
            LastName = nameParts.ElementAtOrDefault(0),
            FirstName = nameParts.ElementAtOrDefault(1),
            DateOfBirth = ParseHl7DateTime(GetField(pid, 7)),
            Sex = GetField(pid, 8)
        };
    }

    private static string FindSegment(IEnumerable<string> segments, string segmentCode)
    {
        return segments.FirstOrDefault(x => x.StartsWith($"{segmentCode}|", StringComparison.OrdinalIgnoreCase))
            ?? throw new FormatException($"HL7 payload does not contain mandatory '{segmentCode}' segment.");
    }

    private static string? GetField(string? segment, int fieldNumber)
    {
        if (string.IsNullOrWhiteSpace(segment))
        {
            return null;
        }

        var fields = segment.Split('|');
        return fields.Length > fieldNumber && !string.IsNullOrWhiteSpace(fields[fieldNumber])
            ? fields[fieldNumber]
            : null;
    }

    private static string? GetMshField(string? segment, int fieldNumber)
    {
        if (string.IsNullOrWhiteSpace(segment))
        {
            return null;
        }

        var fields = segment.Split('|');
        var index = fieldNumber - 1;
        return fields.Length > index && index >= 0 && !string.IsNullOrWhiteSpace(fields[index])
            ? fields[index]
            : null;
    }

    private static string[] SplitComponents(string? componentField)
    {
        return string.IsNullOrWhiteSpace(componentField)
            ? []
            : componentField.Split('^', StringSplitOptions.None);
    }

    private static DateTimeOffset? ParseHl7DateTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTimeOffset.TryParseExact(
                value,
                ["yyyyMMddHHmmss", "yyyyMMddHHmm", "yyyyMMdd"],
                null,
                System.Globalization.DateTimeStyles.AssumeUniversal,
                out var parsed))
        {
            return parsed;
        }

        return null;
    }
}