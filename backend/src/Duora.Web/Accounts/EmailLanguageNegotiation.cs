using Duora.Core.Notifications;
using Microsoft.AspNetCore.Http.Headers;

namespace Duora.Web.Accounts;

/// <summary>
/// Idioma dos e-mails pedidos numa requisição, lido do <c>Accept-Language</c>. A SPA envia o idioma ativo da
/// interface nesse header; sem ele, vale a preferência do navegador. Só pt-BR e en-US passam (qualquer <c>pt-*</c>
/// vira pt-BR e qualquer <c>en-*</c>, en-US), e qualquer outra coisa (header ausente, malformado, idioma não
/// suportado ou só com <c>q=0</c>) cai em en-US (ADR-0026).
/// </summary>
internal static class EmailLanguageNegotiation
{
    public static EmailLanguage FromRequest(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequestHeaders headers = request.GetTypedHeaders();
        // O parser do ASP.NET Core não lança, mas é leniente: um header malformado pode virar lista vazia ou gerar
        // tokens espúrios (em "en;q=2" o "2" vira um token próprio, e o "en" se perde). Por isso todo token passa
        // pela allowlist e o que não casa é descartado; no pior caso o resultado é o padrão, nunca um idioma
        // fora da lista. OrderByDescending é estável: em qualidades iguais, vale a ordem do header.
        return headers.AcceptLanguage
            .Where(range => range.Quality is not 0)
            .OrderByDescending(range => range.Quality ?? 1)
            .Select(range => EmailLanguageTag.Match(range.Value.Value))
            .FirstOrDefault(language => language is not null)
            ?? EmailLanguageTag.Default;
    }
}
