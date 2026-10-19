namespace Duora.Core.Notifications;

/// <summary>
/// Idiomas dos e-mails de serviço, os mesmos da interface (ADR-0022). O valor zero é o padrão: qualquer idioma
/// fora da lista sai em português do Brasil.
/// </summary>
public enum EmailLanguage
{
    BrazilianPortuguese = 0,
    English = 1,
}

/// <summary>
/// Converte entre <see cref="EmailLanguage"/> e a tag BCP 47 que trafega no <c>Accept-Language</c> e no payload
/// da outbox. A tag é dado externo: só o idioma primário conta, e só se estiver na allowlist.
/// </summary>
public static class EmailLanguageTag
{
    public const EmailLanguage Default = EmailLanguage.BrazilianPortuguese;

    private const string BrazilianPortugueseTag = "pt-BR";
    private const string EnglishTag = "en-US";

    /// <summary>
    /// Idioma suportado para a tag, pelo idioma primário (<c>en-US</c> vira inglês; <c>pt-PT</c>, português do
    /// Brasil, como na SPA), ou <c>null</c> quando nenhum serve.
    /// </summary>
    public static EmailLanguage? Match(string? tag)
    {
        if (string.IsNullOrEmpty(tag))
        {
            return null;
        }

        var separator = tag.IndexOf('-', StringComparison.Ordinal);
        var primary = separator < 0 ? tag : tag[..separator];
        if (primary.Equals("pt", StringComparison.OrdinalIgnoreCase))
        {
            return EmailLanguage.BrazilianPortuguese;
        }

        return primary.Equals("en", StringComparison.OrdinalIgnoreCase) ? EmailLanguage.English : null;
    }

    /// <summary>Idioma da tag, ou o padrão quando ela falta (payload anterior ao campo) ou não é suportada.</summary>
    public static EmailLanguage ParseOrDefault(string? tag) => Match(tag) ?? Default;

    public static string Format(EmailLanguage language) => language switch
    {
        EmailLanguage.English => EnglishTag,
        _ => BrazilianPortugueseTag,
    };
}
