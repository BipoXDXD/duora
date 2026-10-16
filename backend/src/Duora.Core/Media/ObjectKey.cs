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
    /// Chave do derivado de um upload: a mesma em toda tentativa, para o Worker que retoma depois de um lease vencido
    /// sobrescrever o objeto em vez de deixar órfãos (spec 16.2). Não precisa ser secreta: nunca sai da API.
    /// </summary>
    public static ObjectKey ForDerivative(Guid uploadId, PhotoVariant variant) =>
        new(StorageArea.Derivatives, $"{DerivativesPrefix}{uploadId:N}-{PhotoVariants.Name(variant)}");

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

        throw new FormatException("Chave de objeto fora do formato gerado pelo servidor.");
    }

    public override string ToString() => Area == StorageArea.Quarantine ? "ObjectKey(quarantine)" : "ObjectKey(derivatives)";

    private static string RandomToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    // \z e não $: o $ do .NET aceita um "\n" no fim, e uma chave com quebra de linha não é chave nossa.
    [GeneratedRegex(@"\Aquarantine/[0-9a-f]{32}\z", RegexOptions.CultureInvariant)]
    private static partial Regex QuarantineKey();

    [GeneratedRegex(@"\Aderivatives/[0-9a-f]{32}-(thumb|card|full)\z", RegexOptions.CultureInvariant)]
    private static partial Regex DerivativeKey();
}
