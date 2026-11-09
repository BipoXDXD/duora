using Duora.Contracts.Participation;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Duora.Web.Http;

/// <summary>
/// Com converter próprio, o gerador mantém o <c>format</c> e perde o <c>type</c>. No contrato o campo segue uma string
/// <c>date-time</c>: o converter só faz valer o offset que esse formato já exige.
/// </summary>
internal static class ExplicitOffsetOpenApi
{
    public static void Configure(OpenApiOptions options) =>
        options.AddSchemaTransformer((schema, context, _) =>
        {
            if (context.JsonPropertyInfo?.CustomConverter is ExplicitOffsetDateTimeOffsetConverter)
            {
                schema.Type = JsonSchemaType.String;
            }

            return Task.CompletedTask;
        });
}
