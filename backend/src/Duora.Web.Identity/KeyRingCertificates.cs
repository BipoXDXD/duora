using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Duora.Web.Identity;

/// <summary>
/// Certificados que cifram o key ring em repouso (spec 11.2). O atual cifra as chaves novas; os anteriores só
/// decifram as chaves que cifraram, durante a troca. Cada segredo é PEM com pares "certificado e, logo depois, a
/// chave privada dele", no formato que o <c>duora-secrets</c> grava.
/// </summary>
internal sealed class KeyRingCertificates : IDisposable
{
    private const string CertificateLabel = "CERTIFICATE";

    private KeyRingCertificates(X509Certificate2? current, IReadOnlyList<X509Certificate2> previous)
    {
        Current = current;
        Previous = previous;
    }

    /// <summary>Nulo quando não há certificado (desenvolvimento e testes): o key ring fica em claro.</summary>
    public X509Certificate2? Current { get; }

    public IReadOnlyList<X509Certificate2> Previous { get; }

    public IEnumerable<X509Certificate2> All => Current is null ? Previous : Previous.Prepend(Current);

    /// <summary>Nulo se algum dos PEM for inválido; a validação das opções dá a mensagem.</summary>
    public static KeyRingCertificates? TryLoad(string? current, string? previous)
    {
        if (!IsValid(current, maximum: 1) || !IsValid(previous))
        {
            return null;
        }

        return new KeyRingCertificates(Parse(current).SingleOrDefault(), Parse(previous));
    }

    /// <summary>Vazio é válido (nenhum certificado); qualquer outro conteúdo precisa formar pares completos.</summary>
    public static bool IsValid(string? pem, int maximum = int.MaxValue)
    {
        try
        {
            var certificates = Parse(pem);
            DisposeAll(certificates);
            return certificates.Count <= maximum;
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException or ArgumentException)
        {
            return false;
        }
    }

    public void Dispose() => DisposeAll(All);

    private static List<X509Certificate2> Parse(string? pem)
    {
        var certificates = new List<X509Certificate2>();
        if (string.IsNullOrWhiteSpace(pem))
        {
            return certificates;
        }

        var blocks = PemBlocks(pem);
        if (blocks.Count == 0 || blocks.Count % 2 != 0)
        {
            throw new FormatException("esperado um ou mais pares de certificado e chave privada");
        }

        try
        {
            for (var i = 0; i < blocks.Count; i += 2)
            {
                certificates.Add(Pair(blocks[i], blocks[i + 1]));
            }
        }
        catch
        {
            DisposeAll(certificates);
            throw;
        }

        return certificates;
    }

    private static X509Certificate2 Pair((string Label, string Text) certificate, (string Label, string Text) key)
    {
        if (certificate.Label != CertificateLabel || !key.Label.EndsWith("PRIVATE KEY", StringComparison.Ordinal))
        {
            throw new FormatException("cada certificado precisa vir seguido da sua chave privada");
        }

        // CreateFromPem recusa chave que não corresponde à chave pública do certificado.
        var pair = X509Certificate2.CreateFromPem(certificate.Text, key.Text);
        if (!pair.HasPrivateKey)
        {
            pair.Dispose();
            throw new FormatException("certificado sem chave privada");
        }

        return pair;
    }

    private static List<(string Label, string Text)> PemBlocks(string pem)
    {
        var blocks = new List<(string, string)>();
        var remaining = pem.AsSpan();
        while (PemEncoding.TryFind(remaining, out var fields))
        {
            blocks.Add((remaining[fields.Label].ToString(), remaining[fields.Location].ToString()));
            remaining = remaining[fields.Location.End..];
        }

        return blocks;
    }

    private static void DisposeAll(IEnumerable<X509Certificate2> certificates)
    {
        foreach (var certificate in certificates)
        {
            certificate.Dispose();
        }
    }
}
