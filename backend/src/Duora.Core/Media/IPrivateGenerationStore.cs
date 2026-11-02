using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Duora.Core.Media;

public enum PrivateGenerationArea { Candidate = 1, Promotion = 2 }

public sealed record PrivateGenerationDomain(Guid Id, long Epoch, int Layout)
{
    public void Validate()
    {
        if (Id == Guid.Empty || Epoch < 1 || Layout != 1)
        {
            throw new InvalidDataException("Invalid private storage domain.");
        }
    }

    public override string ToString() => "PrivateGenerationDomain";
}

public sealed record PrivateGenerationIdentity(
    PrivateGenerationDomain Domain, PrivateGenerationArea Area, Guid Generation, string ManifestHash)
{
    public override string ToString() => "PrivateGenerationIdentity";
}

public sealed record PrivateGenerationVariant(PhotoVariant Kind, long Length, string Sha256);

/// <summary>Exact, bounded storage admission; never a user supplied path or a business approval.</summary>
public sealed record PrivateGenerationManifest(
    PrivateGenerationIdentity Identity, PrivateGenerationVariant[] Variants)
{
    public static PrivateGenerationManifest Create(PrivateGenerationDomain domain, PrivateGenerationArea area,
        Guid generation, IEnumerable<PrivateGenerationVariant> variants)
    {
        ArgumentNullException.ThrowIfNull(variants);
        ArgumentNullException.ThrowIfNull(domain);
        domain.Validate();
        var frozen = variants.ToArray();
        if (!Enum.IsDefined(area) || generation == Guid.Empty
            || frozen.Any(v => v is null || !Enum.IsDefined(v.Kind)))
        {
            throw new InvalidDataException("Invalid private generation shape.");
        }
        Array.Sort(frozen, (left, right) => left.Kind.CompareTo(right.Kind));
        var identity = new PrivateGenerationIdentity(domain, area, generation, Digest(domain, area, generation, frozen));
        var manifest = new PrivateGenerationManifest(identity, frozen);
        manifest.Validate();
        return manifest;
    }

    public void Validate()
    {
        if (Identity is null || Identity.Domain is null || Variants is null)
        {
            throw new InvalidDataException("Missing private manifest.");
        }
        Identity.Domain.Validate();
        if (!Enum.IsDefined(Identity.Area) || Identity.Generation == Guid.Empty || Variants.Length != 3
            || Variants.Any(v => v is null || !Enum.IsDefined(v.Kind) || v.Length is < 1 or > 16_777_216
                || !IsDigest(v.Sha256))
            || Variants.Select(v => v.Kind).Distinct().Count() != 3
            || Variants.Sum(v => v.Length) > 25_165_824 || !IsDigest(Identity.ManifestHash)
            || Identity.ManifestHash != Digest(Identity.Domain, Identity.Area, Identity.Generation, Variants))
        {
            throw new InvalidDataException("Invalid private manifest.");
        }
    }

    public ObjectKey Key(PhotoVariant kind) => Identity.Area switch
    {
        PrivateGenerationArea.Candidate => ObjectKey.ForReviewCandidate(Identity.Generation, kind),
        PrivateGenerationArea.Promotion => ObjectKey.ForPromotedDerivative(Identity.Generation, kind),
        _ => throw new InvalidDataException("Invalid private area."),
    };

    public static bool TryPrivateKey(ObjectKey key, out PrivateGenerationArea area, out Guid generation,
        out PhotoVariant variant)
    {
        area = default;
        generation = default;
        variant = default;
        var value = key.Value;
        if (value is null)
        {
            return false;
        }
        var prefix = value.StartsWith("review-candidates/", StringComparison.Ordinal) ? "review-candidates/"
            : value.StartsWith("derivatives/review-", StringComparison.Ordinal) ? "derivatives/review-" : null;
        if (prefix is null || value.Length <= prefix.Length + 33
            || !Guid.TryParseExact(value.AsSpan(prefix.Length, 32), "N", out generation))
        {
            return false;
        }
        area = prefix == "review-candidates/" ? PrivateGenerationArea.Candidate : PrivateGenerationArea.Promotion;
        var suffix = value[(prefix.Length + 33)..];
        foreach (var kind in PhotoVariants.All)
        {
            if (suffix == PhotoVariants.Name(kind))
            {
                variant = kind;
                return true;
            }
        }
        return false;
    }

    private static bool IsDigest(string value) => value is { Length: 64 }
        && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string Digest(PrivateGenerationDomain domain, PrivateGenerationArea area, Guid generation,
        IEnumerable<PrivateGenerationVariant> variants)
    {
        ArgumentNullException.ThrowIfNull(domain);
        var text = new StringBuilder("duora-private-v1\n").Append(domain.Id.ToString("N")).Append('\n')
            .Append(domain.Epoch.ToString(CultureInfo.InvariantCulture)).Append('\n')
            .Append(domain.Layout.ToString(CultureInfo.InvariantCulture)).Append('\n')
            .Append(((int)area).ToString(CultureInfo.InvariantCulture)).Append('\n')
            .Append(generation.ToString("N")).Append('\n');
        foreach (var variant in variants.OrderBy(v => v.Kind))
        {
            text.Append(PhotoVariants.Name(variant.Kind)).Append('\n')
                .Append(variant.Length.ToString(CultureInfo.InvariantCulture)).Append('\n')
                .Append(variant.Sha256).Append('\n');
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    public override string ToString() => "PrivateGenerationManifest";
}

public sealed record PrivateGenerationCloseReceipt(PrivateGenerationIdentity Identity, long Revision)
{
    public override string ToString() => "PrivateGenerationCloseReceipt";
}

/// <summary>
/// Compulsory boundary for private writes. A closure is distinct from a sweep and from the writer's SQL ACK.
/// Implementations must deny unknown generations and keep exclusion until every admitted I/O has ended.
/// </summary>
public interface IPrivateGenerationStore
{
    PrivateGenerationDomain Domain { get; }
    Task RegisterAsync(PrivateGenerationManifest manifest, CancellationToken cancellationToken);
    Task PutVariantAsync(PrivateGenerationManifest manifest, PhotoVariant kind, Stream content,
        CancellationToken cancellationToken);
    Task SealAsync(PrivateGenerationManifest manifest, CancellationToken cancellationToken);
    Task<PrivateGenerationCloseReceipt> CloseAsync(PrivateGenerationIdentity identity,
        CancellationToken cancellationToken);
    Task<Guid> SweepAsync(PrivateGenerationCloseReceipt receipt, Guid cleanupToken,
        CancellationToken cancellationToken);
}
