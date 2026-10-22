using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Numerics;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Duora.Core.Identity;
using Duora.Core.Notifications;
using Duora.Infrastructure.Email;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;

namespace Duora.IntegrationTests.Outbox;

public sealed class SmtpProtocolTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealTlsAuthAndMimePreserveUtf8AndDeliveryMessageId(bool startTls)
    {
        await using var smtp = new SyntheticSmtp(startTls, trustRoot: true, acceptAuth: true);
        using var services = Services(smtp, startTls);
        Assert.True(EmailAddress.TryParse("recipient@example.invalid", out var to));
        Assert.NotNull(to);
        var mail = new TransactionalEmail(to!, "Duora: código de recuperação / recovery code", "Olá! Synthetic notice only.\nNo token.");
        await services.GetRequiredService<ITransactionalEmailSender>().SendAsync(mail, "aabb1122", TestContext.Current.CancellationToken);
        using var received = await smtp.Result.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.NotNull(received);
        Assert.Equal(mail.Subject, received.Subject);
        Assert.NotNull(received.TextBody);
        Assert.Equal(mail.TextBody, received.TextBody.Replace("\r\n", "\n").TrimEnd('\n'));
        Assert.Equal("duora-aabb1122@example.invalid", received.MessageId);
        Assert.Equal("sender@example.invalid", received.From.Mailboxes.Single().Address);
        Assert.Equal(to.Value, received.To.Mailboxes.Single().Address);
        Assert.True(smtp.Authenticated);
    }

    [Fact]
    public async Task AuthenticationFailureDoesNotSubmitData()
    {
        await using var smtp = new SyntheticSmtp(startTls: false, trustRoot: true, acceptAuth: false);
        using var services = Services(smtp, startTls: false);
        Assert.True(EmailAddress.TryParse("recipient@example.invalid", out var to));
        Assert.NotNull(to);
        await Assert.ThrowsAsync<MailKit.Security.AuthenticationException>(() => services.GetRequiredService<ITransactionalEmailSender>()
            .SendAsync(new TransactionalEmail(to!, "Synthetic", "No token"), "aabb1122", TestContext.Current.CancellationToken));
        Assert.False(smtp.SubmittedData);
    }

    [Fact]
    public async Task UntrustedCertificateIsRejectedBeforeAuthentication()
    {
        await using var smtp = new SyntheticSmtp(startTls: false, trustRoot: false, acceptAuth: true);
        using var services = Services(smtp, startTls: false);
        Assert.True(EmailAddress.TryParse("recipient@example.invalid", out var to));
        Assert.NotNull(to);
        await Assert.ThrowsAsync<MailKit.Security.SslHandshakeException>(() => services.GetRequiredService<ITransactionalEmailSender>()
            .SendAsync(new TransactionalEmail(to!, "Synthetic", "No token"), "aabb1122", TestContext.Current.CancellationToken));
        Assert.False(smtp.Authenticated);
        Assert.False(smtp.SubmittedData);
    }

    private static ServiceProvider Services(SyntheticSmtp server, bool startTls)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Email:Provider"] = "Smtp",
            ["Email:Smtp:Host"] = "localhost",
            ["Email:Smtp:Port"] = server.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Email:Smtp:Security"] = startTls ? "StartTls" : "SslOnConnect",
            ["Email:Smtp:Username"] = "synthetic",
            ["Email:Smtp:Password"] = "synthetic-test-only",
            ["Email:Smtp:From"] = "sender@example.invalid",
        }).Build();
        var services = new ServiceCollection().AddLogging().AddSingleton<IConfiguration>(config);
        services.AddTransactionalEmailDispatch();
        return services.BuildServiceProvider();
    }

    // Disposable server on the hosted test runner only. Trust a unique synthetic root in the runner's
    // CurrentUser store, then remove it. Product TLS certificate validation is never overridden.
    private sealed class SyntheticSmtp : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly TcpListener _crlListener = new(IPAddress.Loopback, 0);
        private readonly Task _crlServer;
        private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(15));
        private readonly X509Certificate2 _root;
        private readonly X509Certificate2 _leaf;
        private readonly bool _trusted;
        public int Port { get; }
        public Task<MimeMessage?> Result { get; }
        public bool Authenticated { get; private set; }
        public bool SubmittedData { get; private set; }

        public SyntheticSmtp(bool startTls, bool trustRoot, bool acceptAuth)
        {
            _crlListener.Start();
            var crlUri = $"http://127.0.0.1:{((IPEndPoint)_crlListener.LocalEndpoint).Port}/synthetic.crl";
            using var rootKey = RSA.Create(2048);
            var rootRequest = new CertificateRequest("CN=Duora synthetic " + Guid.NewGuid(), rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            rootRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(rootRequest.PublicKey, false));
            rootRequest.CertificateExtensions.Add(CertificateRevocationListBuilder.BuildCrlDistributionPointExtension([crlUri]));
            var now = DateTimeOffset.UtcNow;
            _root = rootRequest.CreateSelfSigned(now.AddMinutes(-5), now.AddHours(1));
            using var leafKey = RSA.Create(2048);
            var leafRequest = new CertificateRequest("CN=localhost", leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            leafRequest.CertificateExtensions.Add(CertificateRevocationListBuilder.BuildCrlDistributionPointExtension([crlUri]));
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName("localhost");
            leafRequest.CertificateExtensions.Add(names.Build());
            leafRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            leafRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
            using var signed = leafRequest.Create(_root, now.AddMinutes(-1), now.AddMinutes(30), RandomNumberGenerator.GetBytes(16));
            _leaf = signed.CopyWithPrivateKey(leafKey);
            var crl = new CertificateRevocationListBuilder().Build(
                _root, BigInteger.One, now.AddMinutes(30), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1, now.AddMinutes(-1));
            _crlServer = ServeCrl(crl);
            _trusted = trustRoot;
            if (_trusted)
            {
                using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
                store.Open(OpenFlags.ReadWrite);
                store.Add(_root);
            }
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Result = Serve(startTls, acceptAuth);
        }

        private async Task ServeCrl(byte[] crl)
        {
            var ct = _stop.Token;
            while (!ct.IsCancellationRequested)
            {
                using var request = await _crlListener.AcceptTcpClientAsync(ct);
                await using var stream = request.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                while (await reader.ReadLineAsync(ct) is { Length: > 0 })
                {
                }
                var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/pkix-crl\r\nContent-Length: {crl.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(header, ct);
                await stream.WriteAsync(crl, ct);
                await stream.FlushAsync(ct);
            }
        }

        private async Task<MimeMessage?> Serve(bool startTls, bool acceptAuth)
        {
            var ct = _stop.Token;
            using var client = await _listener.AcceptTcpClientAsync(ct);
            await using var network = client.GetStream();
            using var tls = new SslStream(network, leaveInnerStreamOpen: true);
            if (!startTls)
            {
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = _leaf }, ct);
            }
            Stream stream = startTls ? network : tls;
            var secure = !startTls;
            async Task Write(string line)
            {
                await stream.WriteAsync(Encoding.ASCII.GetBytes(line + "\r\n"), ct);
                await stream.FlushAsync(ct);
            }
            StreamReader Reader() => new(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
            var reader = Reader();
            try
            {
                await Write("220 localhost synthetic test");
                while (await reader.ReadLineAsync(ct) is { } line)
                {
                    if (line.StartsWith("EHLO ", StringComparison.Ordinal))
                    {
                        await Write(secure ? "250-localhost\r\n250 AUTH PLAIN" : "250-localhost\r\n250 STARTTLS");
                    }
                    else if (line == "STARTTLS" && !secure)
                    {
                        await Write("220 Ready for TLS");
                        await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = _leaf }, ct);
                        reader.Dispose();
                        stream = tls;
                        reader = Reader();
                        secure = true;
                    }
                    else if (line.StartsWith("AUTH PLAIN ", StringComparison.Ordinal) && secure)
                    {
                        var supplied = Encoding.UTF8.GetString(Convert.FromBase64String(line[11..]));
                        Authenticated = acceptAuth && supplied == "\0synthetic\0synthetic-test-only";
                        await Write(Authenticated ? "235 Authenticated" : "535 Authentication rejected");
                    }
                    else if (line.StartsWith("MAIL FROM:", StringComparison.Ordinal) || line.StartsWith("RCPT TO:", StringComparison.Ordinal))
                    {
                        if (!Authenticated)
                        {
                            throw new InvalidOperationException("Unauthenticated synthetic envelope");
                        }
                        await Write("250 OK");
                    }
                    else if (line == "DATA")
                    {
                        if (!Authenticated)
                        {
                            throw new InvalidOperationException("Unauthenticated synthetic DATA");
                        }
                        SubmittedData = true;
                        await Write("354 Send message");
                        var body = new StringBuilder();
                        while (await reader.ReadLineAsync(ct) is { } data && data != ".")
                        {
                            body.Append(data.StartsWith("..", StringComparison.Ordinal) ? data[1..] : data).Append("\r\n");
                        }
                        using var bytes = new MemoryStream(Encoding.ASCII.GetBytes(body.ToString()));
                        var message = await MimeMessage.LoadAsync(bytes, ct);
                        await Write("250 Accepted");
                        return message; // Close immediately after DATA acceptance, with no successful QUIT.
                    }
                    else
                    {
                        throw new InvalidOperationException("Unexpected synthetic SMTP command");
                    }
                }
                return null;
            }
            finally { reader.Dispose(); }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            _crlListener.Stop();
            try
            {
                using var message = await Result;
            }
            catch (Exception error) when (error is IOException or AuthenticationException or OperationCanceledException) { }
            finally
            {
                try
                {
                    await _crlServer;
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested)
                {
                }
                finally
                {
                    try
                    {
                        if (_trusted)
                        {
                            using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
                            store.Open(OpenFlags.ReadWrite);
                            store.Remove(_root);
                        }
                    }
                    finally
                    {
                        _root.Dispose();
                        _leaf.Dispose();
                        _stop.Dispose();
                    }
                }
            }
        }
    }
}
