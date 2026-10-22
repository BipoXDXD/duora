using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Duora.Core.Media;

/// <summary>Área do object storage. Originais e derivados ficam separados, com credenciais diferentes (ADR-0025).</summary>
public enum StorageArea
{
    /// <summary>Originais não verificados. Sem rota de leitura na API (TM-44).</summary>
    Quarantine,

    /// <summary>Variantes reencodadas de fotos aprovadas.</summary>
    Derivatives,

    /// <summary>Derivados sanitizados privados de uma tentativa de revisão, nunca publicáveis por esta área.</summary>
    ReviewCandidates,
}

/// <summary>
/// Chave de objeto gerada pelo servidor: área + 128 bits aleatórios (+ variante). Nada do cliente entra nela, nem
/// nome de arquivo (TM-41). É dado interno: nunca vai a DTO, log nem payload de outbox (TM-42), por isso o
/// <see cref="ToString"/> não mostra o valor.
/// </summary>
public readonly partial record struct ObjectKey
{
    private const string QuarantinePrefix = "quarantine/";
    private const string DerivativesPrefix = "derivatives/";
    private const string ReviewCandidatesPrefix = "review-candidates/";

    private ObjectKey(StorageArea area, string value)
    {
        Area = area;
        Value = value;
    }

    public StorageArea Area { get; }

    /// <summary>Caminho relativo no bucket; só para o adaptador de storage e para a coluna do banco.</summary>
    public string Value { get; }

    public static ObjectKey NewQuarantine() => new(StorageArea.Quarantine, QuarantinePrefix + RandomToken());

    /// <summary>
    /// Chave do derivado de um upload: o token aleatório da chave de quarentena (gerado pelo servidor e gravado no
    /// upload, TM-41) mais a variante. É a mesma em toda tentativa, para o Worker que retoma depois de um lease vencido
    /// sobrescrever o objeto em vez de deixar órfãos (spec 16.2), e nunca leva o photoId público.
    /// </summary>
    public static ObjectKey ForDerivative(ObjectKey quarantine, PhotoVariant variant)
    {
        if (quarantine.Area != StorageArea.Quarantine)
        {
            throw new ArgumentException("O derivado nasce da chave de quarentena do upload.", nameof(quarantine));
        }

        var token = quarantine.Value[QuarantinePrefix.Length..];
        return new(StorageArea.Derivatives, $"{DerivativesPrefix}{token}-{PhotoVariants.Name(variant)}");
    }

    /// <summary>
    /// Chave privada de uma geração criada pelo servidor para uma tentativa, sem reutilizar a chave do original.
    /// Quem prepara o candidato gera o UUID aleatório; não recebe um identificador público ou valor do cliente.
    /// </summary>
    public static ObjectKey ForReviewCandidate(Guid generation, PhotoVariant variant)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(generation, Guid.Empty);
        return new(StorageArea.ReviewCandidates, $"{ReviewCandidatesPrefix}{generation:N}-{PhotoVariants.Name(variant)}");
    }

    /// <summary>Lê uma chave gravada pelo próprio sistema; qualquer outro formato é dado corrompido.</summary>
    public static ObjectKey Parse(string stored)
    {
        ArgumentNullException.ThrowIfNull(stored);
        if (QuarantineKey().IsMatch(stored))
        {
            return new ObjectKey(StorageArea.Quarantine, stored);
        }

        if (DerivativeKey().IsMatch(stored))
        {
            return new ObjectKey(StorageArea.Derivatives, stored);
        }

        if (ReviewCandidateKey().IsMatch(stored))
        {
            return new ObjectKey(StorageArea.ReviewCandidates, stored);
        }

        throw new FormatException("Chave de objeto fora do formato gerado pelo servidor.");
    }

    public override string ToString() => Area switch
    {
        StorageArea.Quarantine => "ObjectKey(quarantine)",
        StorageArea.Derivatives => "ObjectKey(derivatives)",
        StorageArea.ReviewCandidates => "ObjectKey(review-candidates)",
        _ => "ObjectKey(invalid)",
    };

    private static string RandomToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    // \z e não $: o $ do .NET aceita um "\n" no fim, e uma chave com quebra de linha não é chave nossa.
    [GeneratedRegex(@"\Aquarantine/[0-9a-f]{32}\z", RegexOptions.CultureInvariant)]
    private static partial Regex QuarantineKey();

    [GeneratedRegex(@"\Aderivatives/[0-9a-f]{32}-(thumb|card|full)\z", RegexOptions.CultureInvariant)]
    private static partial Regex DerivativeKey();

    [GeneratedRegex(@"\Areview-candidates/(?!0{32}-)[0-9a-f]{32}-(thumb|card|full)\z", RegexOptions.CultureInvariant)]
    private static partial Regex ReviewCandidateKey();
}
