using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Duora.Infrastructure.Participation;
using Duora.IntegrationTests;
using Duora.IntegrationTests.Participation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Time.Testing;

namespace Duora.BrowserHost;

internal static class BrowserHostProgram
{
    public static async Task Main()
    {
        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, args) => { args.Cancel = true; shutdown.Cancel(); };
        using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; shutdown.Cancel(); });
        await using var postgres = new PostgresFixture();
        await postgres.InitializeAsync();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var root = FindRepositoryRoot();
        using var files = new PhysicalFileProvider(Path.Combine(root, "frontend", "dist"));
        var control = new BrowserControl(clock, files);
        await using var factory = new DuoraWebFactory(postgres.ConnectionString)
        {
            Origin = "https://localhost:5443",
            ContentRoot = Path.Combine(root, "backend", "src", "Duora.Web"),
            Time = clock,
            TestServices = services => services.AddSingleton<IStartupFilter>(control),
        };
        using var certificate = Certificate();
        factory.UseKestrel(options => options.Listen(IPAddress.Loopback, 5443, listener => listener.UseHttps(certificate)));
        factory.StartServer();
        var seed = await BrowserScenarioSeed.InitializeAsync(postgres.ConnectionString, clock.GetUtcNow(), shutdown.Token);
        control.Seed = new
        {
            seed.Events,
            seed.Rounds,
            accounts = new[]
            {
                new { userId = seed.Alice, email = "alice@example.com", password = "frase longa o bastante" },
                new { userId = seed.Bob, email = "bob@example.com", password = "frase longa o bastante" },
                new { userId = seed.Third, email = "third@example.com", password = "frase longa o bastante" },
            },
        };
        var participation = factory.Services.GetRequiredService<ParticipationService>();
        control.ProcessJobs = async () =>
        {
            for (var count = 0; count < 1000; count++)
            {
                var job = await participation.ClaimJobAsync(shutdown.Token);
                if (job is null)
                {
                    return;
                }
                if (!await participation.CompleteJobAsync(job, shutdown.Token))
                {
                    throw new InvalidOperationException("Browser fixture lost its job lease.");
                }
            }
            throw new InvalidOperationException("Browser fixture exceeded its job processing bound.");
        };
        Console.WriteLine("Browser host ready at https://localhost:5443");
        try
        {
            await Task.Delay(Timeout.Infinite, shutdown.Token);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory()); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Duora.slnx")))
            {
                return directory.Parent!.FullName;
            }
            if (File.Exists(Path.Combine(directory.FullName, "backend", "Duora.slnx")))
            {
                return directory.FullName;
            }
        }
        throw new InvalidOperationException("Run from the Duora repository or backend directory.");
    }

    private static X509Certificate2 Certificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(4));
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), password: null);
    }
}

/// <summary>Only linked into the disposable browser-test process; never into the production application.</summary>
internal sealed class BrowserControl(FakeTimeProvider clock, IFileProvider files) : IStartupFilter
{
    public object? Seed { get; set; }
    public Func<Task>? ProcessJobs { get; set; }

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, following) =>
        {
            if (context.Request.Path == "/__test/seed")
            {
                if (Seed is null)
                {
                    context.Response.StatusCode = 503;
                    return;
                }
                await context.Response.WriteAsJsonAsync(Seed, context.RequestAborted);
                return;
            }
            if (context.Request.Path == "/__test/advance" && HttpMethods.IsPost(context.Request.Method))
            {
                if (!int.TryParse(context.Request.Query["seconds"], out var seconds) || seconds is < 0 or > 86400)
                {
                    context.Response.StatusCode = 400;
                    return;
                }
                clock.Advance(TimeSpan.FromSeconds(seconds));
                if (ProcessJobs is not null)
                {
                    await ProcessJobs();
                }
                context.Response.StatusCode = 204;
                return;
            }
            if (HttpMethods.IsGet(context.Request.Method) && !context.Request.Path.StartsWithSegments("/api")
                && !context.Request.Path.StartsWithSegments("/health") && !context.Request.Path.StartsWithSegments("/assets"))
            {
                context.Request.Path = "/index.html";
            }
            await following();
        });
        app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
        next(app);
    };
}
