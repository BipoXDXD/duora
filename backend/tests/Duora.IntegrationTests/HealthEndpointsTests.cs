using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Duora.IntegrationTests;

public sealed class HealthEndpointsTests(PostgresFixture postgres)
{
    // Porta 1 em loopback: nada escuta ali, então a conexão é recusada na hora.
    private const string UnreachableDatabase = "Host=127.0.0.1;Port=1;Database=duora;Username=duora;Password=x";

    [Fact]
    public async Task LivenessIsOkWithDatabaseUp()
    {
        await using var factory = new DuoraWebFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ReadinessIsOkWithDatabaseUp()
    {
        await using var factory = new DuoraWebFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ReadinessIsServiceUnavailableWhenDatabaseRefusesConnection()
    {
        await using var factory = new DuoraWebFactory(UnreachableDatabase);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task ReadinessGivesUpQuicklyWhenDatabaseAcceptsButNeverAnswers()
    {
        // O kernel completa o handshake TCP pelo backlog, mas ninguém lê nem escreve: um banco travado.
        using var blackhole = new TcpListener(IPAddress.Loopback, 0);
        blackhole.Start();
        var port = ((IPEndPoint)blackhole.LocalEndpoint).Port;
        await using var factory = new DuoraWebFactory(
            $"Host=127.0.0.1;Port={port};Database=duora;Username=duora;Password=x");
        using var client = factory.CreateClient();
        var stopwatch = Stopwatch.StartNew();

        var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ReadinessFailureIsLoggedWithCauseButWithoutThePassword()
    {
        var logs = new CapturingLoggerProvider();
        var connection = new CanaryConnectionString();
        await using var factory = new DuoraWebFactory(connection.Value, logs);
        using var client = factory.CreateClient();

        await client.GetAsync(new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Contains(logs.Entries, entry => entry.Exception is not null);
        Assert.DoesNotContain(logs.Entries, entry => entry.Text.Contains(connection.Password, StringComparison.Ordinal));
    }

    [Fact]
    public async Task LivenessIgnoresTheDatabase()
    {
        await using var factory = new DuoraWebFactory(UnreachableDatabase);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ReadinessFailureBodyIsOnlyTheStatus()
    {
        await using var factory = new DuoraWebFactory(UnreachableDatabase);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Unhealthy", body);
    }
}
