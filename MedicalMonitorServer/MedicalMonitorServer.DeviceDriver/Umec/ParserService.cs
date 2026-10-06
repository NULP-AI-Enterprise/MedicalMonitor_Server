using System.Text;
using System.Text.Json;
using MedicalMonitorServer.Contracts.Models;

namespace MedicalMonitorServer.DeviceDriver.Umec;

public sealed class ParserService
{
    private readonly DeviceState _device;
    private readonly StringBuilder _buffer = new();

    public ParserService(DeviceState device)
    {
        _device = device;
    }

    public IReadOnlyCollection<UmecMessage> Parse(ReadOnlySpan<byte> data)
    {
        _buffer.Append(Encoding.UTF8.GetString(data));
        var messages = new List<UmecMessage>();

        while (true)
        {
            var bufferedData = _buffer.ToString();
            var newlineIndex = bufferedData.IndexOf('\n');
            if (newlineIndex < 0)
            {
                return messages;
            }

            var payload = bufferedData[..newlineIndex].TrimEnd('\r');
            _buffer.Remove(0, newlineIndex + 1);

            if (payload.Length > 0)
            {
                messages.Add(new UmecMessage
                {
                    Device = _device,
                    Payload = payload,
                    Patient = ParsePatient(payload)
                });
            }
        }
    }

    private static PatientData? ParsePatient(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (!root.TryGetProperty("patientId", out var patientIdElement))
            {
                return null;
            }

            var values = new Dictionary<string, string>();
            foreach (var property in root.EnumerateObject())
            {
                if (property.NameEquals("patientId") || property.NameEquals("name"))
                {
                    continue;
                }

                values[property.Name] = property.Value.ToString();
            }

            return new PatientData
            {
                PatientId = patientIdElement.GetString() ?? patientIdElement.ToString(),
                Name = root.TryGetProperty("name", out var nameElement)
                    ? nameElement.GetString()
                    : null,
                Values = values
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}