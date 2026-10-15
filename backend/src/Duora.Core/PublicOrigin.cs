using System.Diagnostics.CodeAnalysis;

namespace Duora.Core;

/// <summary>
/// Origem pública da SPA (esquema, host e porta), de onde vêm as requisições legítimas do navegador e para onde
/// apontam os links dos e-mails. Só https, sem caminho, query, fragmento nem credencial.
/// </summary>
public sealed record PublicOrigin
{
    private readonly Uri _uri;

    private PublicOrigin(Uri uri) => _uri = uri;

    public static bool TryParse(string? value, [NotNullWhen(true)] out PublicOrigin? origin)
    {
        origin = Uri.TryCreate(value, UriKind.Absolute, out var uri) && IsPlainHttpsOrigin(uri)
            ? new PublicOrigin(uri)
            : null;
        return origin is not null;
    }

    public Uri Resolve(string pathAndQuery) => new(_uri, pathAndQuery);

    /// <summary>Forma serializada da origem (RFC 6454), igual ao valor do header <c>Origin</c>.</summary>
    public override string ToString() => _uri.GetLeftPart(UriPartial.Authority);

    private static bool IsPlainHttpsOrigin(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps
        && uri.UserInfo.Length == 0
        && uri.AbsolutePath == "/"
        && uri.Query.Length == 0
        && uri.Fragment.Length == 0;
}
