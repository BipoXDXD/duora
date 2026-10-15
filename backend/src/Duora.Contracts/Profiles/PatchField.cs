using System.Text.Json;
using System.Text.Json.Serialization;

namespace Duora.Contracts.Profiles;

/// <summary>
/// Campo de PATCH com três estados: ausente do corpo (não mexer), presente com <c>null</c> (apagar) e presente com
/// valor (trocar). <c>Nullable&lt;T&gt;</c> e referência anulável juntam os dois primeiros, e a API precisa
/// distingui-los (spec 8.2, API-P02).
/// </summary>
[JsonConverter(typeof(PatchFieldConverterFactory))]
public readonly record struct PatchField<T>
{
    /// <summary>Campo presente no corpo, com valor ou <c>null</c>.</summary>
    public PatchField(T? value)
    {
        IsPresent = true;
        Value = value;
    }

    /// <summary>Falso quando o campo não veio no corpo; o valor padrão do struct é "ausente".</summary>
    public bool IsPresent { get; }

    /// <summary>Valor recebido; <c>null</c> com <see cref="IsPresent"/> verdadeiro significa apagar.</summary>
    public T? Value { get; }
}

/// <summary>
/// O serializador só chama o converter quando a propriedade está no JSON; com <c>HandleNull</c>, o <c>null</c>
/// explícito também chega aqui e vira "presente com null".
/// </summary>
public sealed class PatchFieldConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(PatchField<>);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var valueType = typeToConvert.GetGenericArguments()[0];
        return Activator.CreateInstance(typeof(PatchFieldConverter<>).MakeGenericType(valueType)) as JsonConverter
            ?? throw new InvalidOperationException($"Sem converter para {typeToConvert}.");
    }

    private sealed class PatchFieldConverter<T> : JsonConverter<PatchField<T>>
    {
        public override bool HandleNull => true;

        public override PatchField<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.Null
                ? new PatchField<T>(default)
                : new PatchField<T>(JsonSerializer.Deserialize<T>(ref reader, options));

        public override void Write(Utf8JsonWriter writer, PatchField<T> value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value.Value, options);
    }
}
