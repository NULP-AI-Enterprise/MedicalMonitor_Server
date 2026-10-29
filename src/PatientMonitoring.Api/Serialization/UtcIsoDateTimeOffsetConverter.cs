using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PatientMonitoring.Api.Serialization;

/// <summary>
/// Writes timestamps in the contract format: ISO 8601 in UTC with milliseconds and a Z suffix,
/// e.g. "2026-09-27T14:15:30.500Z". Reads any ISO 8601 value, with or without an offset.
/// </summary>
public sealed class UtcIsoDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        DateTimeOffset.Parse(
            reader.GetString() ?? throw new JsonException("A timestamp is required."),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal);

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
}
