using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Duora.Web.Http;

/// <summary>
/// Toda operação da API declara o 503 de banco indisponível com o <c>Retry-After</c> (#492). Qualquer rota pode
/// recebê-lo: o cookie de sessão é validado no banco a cada requisição, inclusive nas rotas anônimas, e o handler global
/// (<see cref="DatabaseUnavailableExceptionHandler"/>) responde 503 <c>TEMPORARILY_UNAVAILABLE</c> sem que a rota o
/// declare. As rotas que já declaram o 503 mantêm a descrição e o corpo; só ganham o header que faltava.
/// </summary>
internal static class ServiceUnavailableOpenApi
{
    private const string UnavailableStatus = "503";

    private const string RetryAfterDescription =
        "Seconds to wait before trying again. Sent with TEMPORARILY_UNAVAILABLE; other 503 problems of a route may omit it.";

    public static void Configure(OpenApiOptions options) =>
        options.AddOperationTransformer((operation, context, _) =>
        {
            operation.Responses ??= [];
            if (!operation.Responses.TryGetValue(UnavailableStatus, out var response))
            {
                // Referência ao componente que as outras respostas de erro já usam; o schema inline duplicaria o corpo.
                var problem = new OpenApiSchemaReference(nameof(ProblemDetails), context.Document);
                response = new OpenApiResponse
                {
                    Description = "Service Unavailable",
                    Content = new Dictionary<string, OpenApiMediaType>
                    {
                        ["application/problem+json"] = new OpenApiMediaType { Schema = problem },
                    },
                };
                operation.Responses[UnavailableStatus] = response;
            }

            if (response is OpenApiResponse declared)
            {
                declared.Headers ??= new Dictionary<string, IOpenApiHeader>();
                declared.Headers.TryAdd("Retry-After", new OpenApiHeader
                {
                    Description = RetryAfterDescription,
                    Schema = new OpenApiSchema { Type = JsonSchemaType.Integer, Minimum = "1" },
                });
            }

            return Task.CompletedTask;
        });
}
