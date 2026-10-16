using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Duora.IntegrationTests;

/// <summary>Certificados descartáveis no formato do segredo do deploy: certificado e chave PKCS#8 num só PEM.</summary>
internal static class TestCertificates
{
    private const int RsaKeyBits = 2048;

    public static TestCertificate Create(string name)
    {
        using var rsa = RSA.Create(RsaKeyBits);
        var request = new CertificateRequest($"CN={name}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var now = DateTimeOffset.UtcNow;
        using var certificate = request.CreateSelfSigned(now.AddMinutes(-5), now.AddYears(1));
        return new TestCertificate(
            certificate.ExportCertificatePem() + "\n" + rsa.ExportPkcs8PrivateKeyPem() + "\n",
            Convert.ToBase64String(certificate.RawData));
    }

    public static TestCertificate CreateEcdsa(string name)
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={name}", ecdsa, HashAlgorithmName.SHA256);
        var now = DateTimeOffset.UtcNow;
        using var certificate = request.CreateSelfSigned(now.AddMinutes(-5), now.AddYears(1));
        return new TestCertificate(
            certificate.ExportCertificatePem() + "\n" + ecdsa.ExportPkcs8PrivateKeyPem() + "\n",
            Convert.ToBase64String(certificate.RawData));
    }
}

/// <param name="Pem">Conteúdo do arquivo de segredo.</param>
/// <param name="RawBase64">Certificado em base64, como o EncryptedXml o grava no <c>X509Certificate</c>.</param>
internal sealed record TestCertificate(string Pem, string RawBase64);
