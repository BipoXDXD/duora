using Duora.Contracts.Profiles;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Duora.Web.Http;

/// <summary>
/// O <see cref="PatchField{T}"/> tem converter próprio, e o gerador o descreveria como objeto vazio. No contrato
/// ele é o próprio valor, anulável, e fica inline em cada propriedade para receber os limites dela.
/// </summary>
internal static class PatchFieldOpenApi
{
    public static void Configure(OpenApiOptions options)
    {
        options.CreateSchemaReferenceId = type =>
            IsPatchField(type.Type) ? null : OpenApiOptions.CreateDefaultSchemaReferenceId(type);
        options.AddSchemaTransformer((schema, context, _) =>
        {
            var type = context.JsonTypeInfo.Type;
            if (IsPatchField(type))
            {
                schema.Type = type.GetGenericArguments()[0] == typeof(string)
                    ? JsonSchemaType.String | JsonSchemaType.Null
                    : throw new NotSupportedException($"PatchField de {type.GetGenericArguments()[0]} sem schema definido.");
                schema.Description = null;
            }

            return Task.CompletedTask;
        });
    }

    private static bool IsPatchField(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(PatchField<>);
}
