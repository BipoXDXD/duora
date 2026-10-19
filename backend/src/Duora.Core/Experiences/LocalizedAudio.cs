using System.Security.Cryptography;

namespace Duora.Core.Experiences;

/// <summary>Publication metadata for an offline audio asset. Bytes are verified before catalog admission.</summary>
public sealed record LocalizedAudioVariant
{
    public LocalizedAudioVariant(string assetId, string transcript, string accessibleDescription,
        string provenance, string sha256, ReadOnlyMemory<byte> bytes)
    {
        AssetId = Slug.Require(assetId, nameof(assetId));
        Transcript = CatalogText.Require(transcript, 4000, nameof(transcript));
        AccessibleDescription = CatalogText.Require(accessibleDescription, 1000, nameof(accessibleDescription));
        Provenance = CatalogText.Require(provenance, 1000, nameof(provenance));
        if (bytes.IsEmpty || !string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes.Span)), sha256, StringComparison.Ordinal))
        {
            throw new ArgumentException("Audio bytes must match the manifest SHA-256.", nameof(sha256));
        }
        Sha256 = sha256;
    }

    public string AssetId { get; }
    public string Transcript { get; }
    public string AccessibleDescription { get; }
    public string Provenance { get; }
    public string Sha256 { get; }
}

public sealed record LocalizedAudio
{
    public LocalizedAudio(string cueId, LocalizedAudioVariant enUS, LocalizedAudioVariant ptBR)
    {
        CueId = Slug.Require(cueId, nameof(cueId));
        ArgumentNullException.ThrowIfNull(enUS);
        ArgumentNullException.ThrowIfNull(ptBR);
        EnUS = enUS;
        PtBR = ptBR;
    }
    public string CueId { get; }
    public LocalizedAudioVariant EnUS { get; }
    public LocalizedAudioVariant PtBR { get; }

    public LocalizedAudioView ForLocale(string locale)
    {
        var variant = LocalizedCatalog.ForLocale(locale, EnUS, PtBR);
        return new(CueId, variant.AssetId, variant.Transcript, variant.AccessibleDescription,
            variant.Provenance, variant.Sha256);
    }
}

/// <summary>A shared catalog cue in one viewer's locale. Contains neither another locale nor audio bytes.</summary>
public sealed record LocalizedAudioView(string CueId, string AssetId, string Transcript,
    string AccessibleDescription, string Provenance, string Sha256);

internal static class LocalizedCatalog
{
    public static T ForLocale<T>(string locale, T enUS, T ptBR) => locale switch
    {
        "en-US" => enUS,
        "pt-BR" => ptBR,
        _ => throw new ArgumentException("Unsupported display locale.", nameof(locale)),
    };

    public static void RequireStructure(IEnumerable<string> enUS, IEnumerable<string> ptBR)
    {
        if (!enUS.SequenceEqual(ptBR, StringComparer.Ordinal))
        {
            throw new ArgumentException("Localized content must preserve the same ordered structure and IDs.");
        }
    }

    public static IReadOnlyList<LocalizedAudio> RequireAudio(IEnumerable<LocalizedAudio>? audio)
    {
        var entries = (audio ?? []).ToArray();
        if (entries.Any(entry => entry is null) || entries.Select(entry => entry.CueId).Distinct().Count() != entries.Length)
        {
            throw new ArgumentException("Audio cue IDs must be unique.", nameof(audio));
        }

        return Array.AsReadOnly(entries);
    }
}
