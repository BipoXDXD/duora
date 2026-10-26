namespace Duora.Infrastructure.Identity;

internal enum LegalDocument
{
    Terms,
    Privacy,
}

/// <summary>
/// Aceite de uma versão de documento legal no cadastro (spec 10.2, <c>consent_records</c>). Mapeada para o schema;
/// gravada por SQL no cadastro, junto com o upsert da conta.
/// </summary>
internal sealed class ConsentRecord
{
    private ConsentRecord()
    {
    }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public LegalDocument Document { get; private set; }

    public string Version { get; private set; } = "";

    /// <summary>Language of this accepted document. Null is unknown, including legacy evidence.</summary>
    public string? DocumentLocale { get; private set; }

    /// <summary>Exact content fingerprint. Historical unknown values remain null.</summary>
    public string? DocumentSha256 { get; private set; }

    public DateTimeOffset AcceptedAt { get; private set; }
}
