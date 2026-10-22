using System.Net;
using System.Net.Sockets;
using Duora.Core.Identity;
using Duora.Core.Notifications;
using Duora.Infrastructure.Email;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Duora.IntegrationTests.Outbox;

/// <summary>Exercises transport selection through the real composition root, without a mocked sender.</summary>
public sealed class SmtpTransportTests
{
    [Fact]
    public async Task SelectingSmtpConnectsToTheConfiguredServerInsteadOfWritingAPickupFile()
    {
        var directory = Directory.CreateTempSubdirectory("duora-smtp-test-");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var services = CreateServices(directory.FullName, ((IPEndPoint)listener.LocalEndpoint).Port);
        try
        {
            var connection = listener.AcceptTcpClientAsync(timeout.Token).AsTask();
            var sending = services.GetRequiredService<ITransactionalEmailSender>()
                .SendAsync(Email(), "0123456789abcdef", timeout.Token);

            // Pickup completes without opening a connection. SMTP must establish its configured TLS socket.
            Assert.Same(connection, await Task.WhenAny(connection, sending));
            using var client = await connection;
            Assert.Empty(Directory.GetFiles(directory.FullName, "*.eml"));
            timeout.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
        }
        finally
        {
            timeout.Cancel();
            directory.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("Email:Transport", "Unknown")]
    [InlineData("Email:Smtp:Host", "")]
    [InlineData("Email:Smtp:Security", "None")]
    public async Task InvalidSmtpConfigurationIsRejectedWithoutWritingAPickupFile(string key, string value)
    {
        var directory = Directory.CreateTempSubdirectory("duora-smtp-invalid-");
        try
        {
            using var services = CreateServices(directory.FullName, 465, key, value);
            await Assert.ThrowsAsync<OptionsValidationException>(() =>
                services.GetRequiredService<ITransactionalEmailSender>()
                    .SendAsync(Email(), "0123456789abcdef", TestContext.Current.CancellationToken));
            Assert.Empty(Directory.GetFiles(directory.FullName, "*.eml"));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static ServiceProvider CreateServices(string pickupDirectory, int port, string? overrideKey = null, string? overrideValue = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["Email:Transport"] = "Smtp",
            ["Email:PickupDirectory"] = pickupDirectory,
            ["Email:Smtp:Host"] = "localhost",
            ["Email:Smtp:Port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Email:Smtp:Security"] = "SslOnConnect",
            ["Email:Smtp:Username"] = "synthetic-user",
            ["Email:Smtp:Password"] = "synthetic-only-not-a-real-credential",
            ["Email:Smtp:From"] = "sender@example.invalid",
        };
        if (overrideKey is not null)
        {
            values[overrideKey] = overrideValue;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddTransactionalEmailDispatch();
        return services.BuildServiceProvider();
    }

    private static TransactionalEmail Email()
    {
        Assert.True(EmailAddress.TryParse("recipient@example.invalid", out var address));
        Assert.NotNull(address);
        return new TransactionalEmail(
            address,
            "Duora synthetic SMTP test",
            "Synthetic data only. No account token or participant data.");
    }
}
