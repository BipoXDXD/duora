namespace Duora.Core.Notifications;

/// <summary>
/// Idiomas dos e-mails de serviço, os mesmos da interface (ADR-0022, tags do ADR-0026). O padrão para idioma fora
/// da lista é <see cref="EmailLanguageTag.Default"/>, não o valor zero.
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
    /// <summary>Fallback para idioma fora da lista: o público principal não é brasileiro (ADR-0026).</summary>
    public const EmailLanguage Default = EmailLanguage.English;

    private const string BrazilianPortugueseTag = "pt-BR";
    private const string EnglishTag = "en-US";

    /// <summary>
    /// Idioma suportado para a tag, pelo idioma primário (<c>en-GB</c> vira inglês; <c>pt-PT</c>, português do
    /// Brasil, como na SPA), ou <c>null</c> quando nenhum serve. Por isso o <c>en</c> que a outbox gravava antes do
    /// ADR-0026 continua lido como inglês, sem migração dos pedidos pendentes.
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

    /// <summary>
    /// Idioma do pedido da outbox. Sem tag é payload anterior ao campo, gravado quando só havia pt-BR, e continua
    /// pt-BR (<see cref="Format"/> sempre grava uma tag); tag não suportada cai no <see cref="Default"/>.
    /// </summary>
    public static EmailLanguage ParseOrDefault(string? tag) =>
        tag is null ? EmailLanguage.BrazilianPortuguese : Match(tag) ?? Default;

    public static string Format(EmailLanguage language) => language switch
    {
        EmailLanguage.English => EnglishTag,
        EmailLanguage.BrazilianPortuguese => BrazilianPortugueseTag,
        _ => throw new ArgumentOutOfRangeException(nameof(language), language, "Idioma sem tag."),
    };
}
