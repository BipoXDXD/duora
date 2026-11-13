using System.Text.Json;
using System.Text.Json.Serialization;

namespace Duora.Contracts.Participation;

/// <summary>
/// Recusa instante sem offset, que o leitor padrão interpretaria no fuso do servidor, e offset sem minutos,
/// que ele aceita. O contrato publicado é <c>date-time</c> (RFC 3339), que exige <c>Z</c> ou <c>±hh:mm</c>.
/// </summary>
public sealed class ExplicitOffsetDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String && reader.TryGetDateTimeOffset(out var value) && HasRfc3339Offset(reader.GetString()!))
        {
            return value;
        }

        throw new JsonException("The value must be a date-time with an explicit offset.");
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);

    private static bool HasRfc3339Offset(string text) => text.EndsWith('Z') || text is [.., '+' or '-', _, _, ':', _, _];
}
