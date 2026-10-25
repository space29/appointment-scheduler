using System.Text.Json;
using System.Text.Json.Serialization;

namespace Scheduler.Api;

// DateTimeOffset's normal parser also accepts missing offsets. The API requires an explicit instant.
public sealed class ExplicitOffsetConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        var text = reader.GetString();
        var t = text?.IndexOf('T') ?? -1;
        if (t < 0 || !(text!.EndsWith('Z') || text[(t + 1)..].Contains('+') || text[(t + 1)..].Contains('-')) ||
            !DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var result))
            throw new JsonException("An ISO 8601 timestamp with an explicit offset is required.");
        return result;
    }
    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) => writer.WriteStringValue(value);
}
