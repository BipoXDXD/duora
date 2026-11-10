using System.Text.Json;
using System.Text.Json.Serialization;

namespace Duora.Contracts.Participation;

/// <summary>
/// Recusa instante sem offset, que o leitor padrão interpretaria no fuso do servidor.
/// O contrato publicado é <c>date-time</c> (RFC 3339), que exige offset.
/// </summary>
public sealed class ExplicitOffsetDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
{
    /// <summary>Tamanho de <c>yyyy-MM-dd</c>: depois da data, um hífen só pode ser offset.</summary>
    private const int DateLength = 10;

    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String && reader.TryGetDateTimeOffset(out var value) && HasExplicitOffset(reader.GetString()!))
        {
            return value;
        }

        throw new JsonException("The value must be a date-time with an explicit offset.");
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);

    private static bool HasExplicitOffset(string text) => text.AsSpan(DateLength).IndexOfAny('Z', '+', '-') >= 0;
}
