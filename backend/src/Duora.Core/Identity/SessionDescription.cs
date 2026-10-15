namespace Duora.Core.Identity;

/// <summary>
/// Descrição mínima de uma sessão para a lista "onde estou conectado" (API-A09): só família do navegador e do
/// sistema. O User-Agent bruto não é guardado nem devolvido (dado do cliente, potencialmente identificador).
/// </summary>
public static class SessionDescription
{
    private const string Unknown = "Dispositivo desconhecido";

    // A ordem importa: o Edge também anuncia "Chrome/", o Chrome também anuncia "Safari/", o Android também
    // anuncia "Linux" e o iPhone também anuncia "Mac OS X".
    private static readonly (string Marker, string Name)[] Browsers =
    [
        ("Edg/", "Edge"),
        ("OPR/", "Opera"),
        ("Firefox/", "Firefox"),
        ("Chrome/", "Chrome"),
        ("Safari/", "Safari"),
    ];

    private static readonly (string Marker, string Name)[] Systems =
    [
        ("Android", "Android"),
        ("iPhone", "iOS"),
        ("iPad", "iOS"),
        ("Windows", "Windows"),
        ("Mac OS X", "macOS"),
        ("CrOS", "ChromeOS"),
        ("Linux", "Linux"),
    ];

    public static string FromUserAgent(string? userAgent)
    {
        if (string.IsNullOrEmpty(userAgent))
        {
            return Unknown;
        }

        var browser = FirstMatch(Browsers, userAgent);
        var system = FirstMatch(Systems, userAgent);
        return (browser, system) switch
        {
            (null, null) => Unknown,
            (null, _) => $"Navegador em {system}",
            (_, null) => browser,
            _ => $"{browser} em {system}",
        };
    }

    private static string? FirstMatch((string Marker, string Name)[] table, string userAgent) =>
        table.FirstOrDefault(entry => userAgent.Contains(entry.Marker, StringComparison.Ordinal)).Name;
}
