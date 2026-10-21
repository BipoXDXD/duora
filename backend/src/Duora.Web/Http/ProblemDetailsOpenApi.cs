using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Duora.Web.Http;

/// <summary>Descreve as extensões emitidas pelo escritor de problemas sem alterar a serialização nem abrir os DTOs.</summary>
internal static class ProblemDetailsOpenApi
{
    public static void Configure(OpenApiOptions options) =>
        options.AddSchemaTransformer((schema, context, _) =>
        {
            var type = context.JsonTypeInfo.Type;
            if (type == typeof(ProblemDetails) || type == typeof(HttpValidationProblemDetails))
            {
                schema.Properties ??= new Dictionary<string, IOpenApiSchema>();
                schema.Properties["code"] = new OpenApiSchema { Type = JsonSchemaType.String };
                schema.Properties["traceId"] = new OpenApiSchema { Type = JsonSchemaType.String };
            }

            return Task.CompletedTask;
        });
}
