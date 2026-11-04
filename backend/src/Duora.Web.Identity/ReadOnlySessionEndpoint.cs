using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Duora.Web.Identity;

/// <summary>
/// Rota que lê a sessão sem escrever nela (ADR-0048, issue #321): o ticket store valida a sessão com o mesmo critério
/// de toda requisição, mas sem registrar o uso nem deslizar a expiração ociosa, e recusa renovar, gravar ou revogar.
/// Exceção fechada: só a prévia da reconciliação de inscrições a usa (lista em <c>ReadOnlySessionTests</c>).
/// </summary>
public sealed class ReadOnlySessionEndpoint
{
    /// <summary>A requisição já foi roteada para uma rota marcada; sem rota, vale o toque comum.</summary>
    public static bool Applies(HttpContext? context) =>
        context?.GetEndpoint()?.Metadata.GetMetadata<ReadOnlySessionEndpoint>() is not null;
}

public static class ReadOnlySessionEndpointExtensions
{
    /// <summary>Marca a rota como leitura de sessão sem toque. Exige o roteamento antes da autenticação.</summary>
    public static TBuilder WithReadOnlySession<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new ReadOnlySessionEndpoint());
}
