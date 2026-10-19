extern alias worker;

using Duora.Core.Identity;
using Duora.Infrastructure.Identity;
using Duora.IntegrationTests.Outbox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using worker::Duora.Worker;

namespace Duora.IntegrationTests.AgeVerification;

/// <summary>
/// Banco migrado só deste teste (o consumidor pega qualquer entrega da fila), uma conta <c>PendingEligibility</c> com
/// uma solicitação <c>Pending</c> e o evento da outbox, como API-P05 grava. O worker é o host real do
/// <see cref="WorkerHost"/>, não iniciado: o teste chama o dispatcher para controlar os passos.
/// </summary>
internal sealed class AgeVerificationOutboxHarness : IAsyncDisposable
{
    private readonly List<IHost> _hosts = [];
    private readonly DirectoryInfo _mailDirectory = Directory.CreateTempSubdirectory("duora-mail-");

    private AgeVerificationOutboxHarness(string connectionString, TimeProvider time)
    {
        ConnectionString = connectionString;
        Time = time;
    }

    public string ConnectionString { get; }

    public TimeProvider Time { get; }

    public FakeTimeProvider FakeTime => Time as FakeTimeProvider
        ?? throw new InvalidOperationException("Este harness usa o relógio do sistema.");

    /// <summary>Com <c>realClock</c>, relógio do sistema, para os testes que dependem de timeout de verdade.</summary>
    public static async Task<AgeVerificationOutboxHarness> CreateAsync(
        PostgresFixture postgres,
        CancellationToken cancellationToken,
        bool realClock = false)
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync(cancellationToken);
        TimeProvider time = realClock ? TimeProvider.System : new FakeTimeProvider(DateTimeOffset.UtcNow);
        return new AgeVerificationOutboxHarness(connectionString, time);
    }

    public AgeVerificationDispatcher StartWorker(string mode = "Succeed", params string[] extraArgs) =>
        StartWorkerWithProvider(null, mode, extraArgs);

    public AgeVerificationDispatcher StartWorkerWithProvider(
        IAgeEligibilityProvider? provider, string mode = "Succeed", params string[] extraArgs)
    {
        string[] args =
        [
            "--environment", "Testing",
            $"--ConnectionStrings:Duora={ConnectionString}",
            $"--Duora:PublicOrigin={EmailOutboxHarness.PublicOrigin}",
            $"--Email:PickupDirectory={_mailDirectory.FullName}",
            .. AgeVerificationScenario.FakeProviderSettings(mode).Select(setting => $"--{setting.Key}={setting.Value}"),
            .. extraArgs,
        ];
        var builder = WorkerHost.CreateBuilder(args);
        builder.Services.AddSingleton(Time);
        if (provider is not null)
        {
            builder.Services.AddSingleton(provider);
        }

        var host = builder.Build();
        _hosts.Add(host);
        return host.Services.GetRequiredService<AgeVerificationDispatcher>();
    }

    /// <summary>Conta pendente com solicitação aberta e o pedido ao fornecedor na outbox.</summary>
    public async Task<PendingVerification> CreatePendingVerificationAsync(CancellationToken cancellationToken)
    {
        var now = Time.GetUtcNow();
        var userId = Guid.CreateVersion7(now);
        var verificationId = Guid.CreateVersion7(now);
        var email = $"pessoa-{userId:N}@example.com";
        await ExecuteAsync(
            """
            insert into identity.users
                (id, user_name, normalized_user_name, email, normalized_email, email_confirmed, security_stamp,
                 concurrency_stamp, phone_number_confirmed, two_factor_enabled, lockout_enabled, access_failed_count,
                 status, created_at)
            values ($1, $2, upper($2), $2, upper($2), true, 'stamp', 'concurrency', false, false, true, 0,
                    'PendingEligibility', $3)
            """,
            cancellationToken,
            userId,
            email,
            now);
        await ExecuteAsync(
            """
            insert into identity.age_verifications
                (id, user_id, state, policy_version, provider, request_nonce, requested_at, version)
            values ($1, $2, 'Pending', 'test-2026-10', 'fake', $3, $4, 0)
            """,
            cancellationToken,
            verificationId,
            userId,
            $"nonce-{verificationId:N}",
            now);

        await using var dbContext = PostgresFixture.CreateDbContext(ConnectionString);
        var outboxEvent = AgeVerificationOutbox.Create(
            new AgeVerificationRequested(verificationId),
            Guid.CreateVersion7(now),
            now,
            now + TimeSpan.FromHours(24));
        dbContext.Add(outboxEvent);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new PendingVerification(userId, verificationId, outboxEvent.Deliveries.Single().Id);
    }

    public async Task<DeliveryRow> ReadDeliveryAsync(Guid deliveryId, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select d.status, d.attempts, d.fencing_token, d.available_at, d.last_error, e.payload::text
            from ops.outbox_deliveries d
            join ops.outbox_events e on e.id = d.outbox_event_id
            where d.id = $1
            """,
            connection);
        command.Parameters.AddWithValue(deliveryId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken), $"Entrega {deliveryId} não existe.");
        return new DeliveryRow(
            reader.GetString(0),
            reader.GetInt32(1),
            reader.GetInt64(2),
            reader.GetFieldValue<DateTimeOffset>(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetString(5));
    }

    public async Task<VerificationRow> ReadVerificationAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return Assert.Single(await AgeVerificationScenario.ReadVerificationsAsync(connection, userId));
    }

    public async Task<string> AccountStatusAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("select status from identity.users where id = $1", connection);
        command.Parameters.AddWithValue(userId);
        return (string?)await command.ExecuteScalarAsync(cancellationToken) ?? throw new InvalidOperationException();
    }

    public async Task ExecuteAsync(string sql, CancellationToken cancellationToken, params object[] parameters)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter);
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts)
        {
            host.Dispose();
        }

        await Task.Yield();
        _mailDirectory.Delete(recursive: true);
    }
}

internal sealed record PendingVerification(Guid UserId, Guid VerificationId, Guid DeliveryId);
