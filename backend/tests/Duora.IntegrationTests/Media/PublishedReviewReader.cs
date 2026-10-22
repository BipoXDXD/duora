using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Duora.IntegrationTests.Identity;

namespace Duora.IntegrationTests.Media;

/// <summary>Unmodified published Web binary, not the assembly used by WebApplicationFactory.</summary>
internal sealed class PublishedReviewReader(IContainer container, byte[] certificateHash) : IAsyncDisposable
{
    internal const string Commit = "2ab2593d5d1312f56e71cd091bfe0167b1bcd685";
    internal const string Image = "ghcr.io/bipoxdxd/duora-web@sha256:92651e1fc7c78b4686d9e85adc2bbcd5bae83951b7b577724f665a84bd1dbad5";
    private const int Port = 8443;

    internal static async Task<PublishedReviewReader> StartAsync(INetwork network, string connectionString, int queuedLimit = 100)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        var container = new ContainerBuilder(Image)
            .WithNetwork(network)
            .WithPortBinding(Port, assignRandomHostPort: true)
            .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Testing")
            .WithEnvironment("ASPNETCORE_URLS", $"https://+:{Port}")
            .WithEnvironment("Kestrel__Certificates__Default__Path", "/fixture/server.pfx")
            .WithResourceMapping(certificate.Export(X509ContentType.Pfx), "/fixture/server.pfx",
                fileMode: UnixFileModes.UserRead | UnixFileModes.GroupRead | UnixFileModes.OtherRead)
            .WithEnvironment("ConnectionStrings__Duora", connectionString)
            .WithEnvironment("Duora__PublicOrigin", DuoraWebFactory.PublicOrigin)
            .WithEnvironment("DataProtection__RequireMountedVolume", "false")
            .WithEnvironment("DataProtection__RequireCertificate", "false")
            .WithEnvironment("DataProtection__KeysDirectory", "/home/app/.aspnet/DataProtection-Keys")
            .WithEnvironment("Media__Review", "TechnicalOnly")
            .WithEnvironment("Media__Storage__Provider", "FileSystem")
            .WithEnvironment("Media__Storage__FileSystemRoot", "/tmp/synthetic-media")
            .WithEnvironment("Media__MaxQueuedUploads", queuedLimit.ToString(CultureInfo.InvariantCulture))
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Now listening on:"))
            .Build();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            await container.StartAsync(timeout.Token);
            return new PublishedReviewReader(container, certificate.GetCertHash(HashAlgorithmName.SHA256));
        }
        catch
        {
            await container.DisposeAsync();
            throw;
        }
    }

    internal Browser CreateBrowser()
    {
        var cookies = new CookieContainer();
        var handler = new HttpClientHandler
        {
            CookieContainer = cookies,
            AllowAutoRedirect = false,
            // Trust only the ephemeral certificate created for this synthetic container.
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) => certificate is not null
                && CryptographicOperations.FixedTimeEquals(certificate.GetCertHash(HashAlgorithmName.SHA256), certificateHash),
        };
        var http = new HttpClient(handler)
        {
            BaseAddress = new UriBuilder("https", container.Hostname, container.GetMappedPublicPort(Port)).Uri,
            Timeout = TimeSpan.FromSeconds(20),
        };
        return new Browser(http, cookies);
    }

    public ValueTask DisposeAsync() => container.DisposeAsync();
}
