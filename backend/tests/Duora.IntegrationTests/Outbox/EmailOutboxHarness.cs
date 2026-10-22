extern alias worker;

using Duora.Core.Notifications;
using Duora.Infrastructure.Email;
using Duora.Infrastructure.Outbox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using worker::Duora.Worker;

namespace Duora.IntegrationTests.Outbox;

/// <summary>
/// Banco migrado só deste teste, diretório de e-mail temporário e relógio falso compartilhado pelos workers
/// que o teste sobe. Cada worker é o host real do <see cref="WorkerHost"/>, não iniciado: o teste chama o
/// dispatcher diretamente para controlar a ordem dos passos.
/// </summary>
internal sealed class EmailOutboxHarness : IAsyncDisposable
{
    public const string PublicOrigin = "https://duora.test";

    private readonly List<IHost> _hosts = [];

    private EmailOutboxHarness(string connectionString, DirectoryInfo mailDirectory)
    {
        ConnectionString = connectionString;
        MailDirectory = mailDirectory;
    }

    public string ConnectionString { get; }

    public DirectoryInfo MailDirectory { get; }

    // Parte do instante real só para as chaves de Data Protection e afins não ficarem "no futuro"; os testes
    // dependem apenas dos avanços explícitos.
    public FakeTimeProvider Time { get; } = new(DateTimeOffset.UtcNow);

    public static async Task<EmailOutboxHarness> CreateAsync(PostgresFixture postgres, CancellationToken cancellationToken)
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync(cancellationToken);
        return new EmailOutboxHarness(connectionString, Directory.CreateTempSubdirectory("duora-mail-"));
    }

    public TransactionalEmailDispatcher StartWorker(
        string[]? extraArgs = null,
        Func<ITransactionalEmailSender, ITransactionalEmailSender>? decorateSender = null,
        ILoggerProvider? logs = null,
        Action<IServiceCollection>? configureServices = null)
    {
        string[] args =
        [
            "--environment", "Testing",
            $"--ConnectionStrings:Duora={ConnectionString}",
            $"--Duora:PublicOrigin={PublicOrigin}",
            $"--Email:PickupDirectory={MailDirectory.FullName}",
            .. extraArgs ?? [],
        ];
        var builder = WorkerHost.CreateBuilder(args);
        builder.Services.AddSingleton<TimeProvider>(Time);
        if (decorateSender is not null)
        {
            builder.Services.AddSingleton(provider => decorateSender(new PickupDirectoryEmailSender(
                provider.GetRequiredService<IOptions<PickupDirectoryOptions>>(),
                provider.GetRequiredService<ILogger<PickupDirectoryEmailSender>>())));
        }

        if (logs is not null)
        {
            builder.Logging.AddProvider(logs);
        }

        configureServices?.Invoke(builder.Services);
        var host = builder.Build();
        _hosts.Add(host);
        return host.Services.GetRequiredService<TransactionalEmailDispatcher>();
    }

    public Task<Guid> EnqueueAsync(AccountEmailKind kind, string? token, CancellationToken cancellationToken) =>
        EnqueueAsync(kind, token, "pt-BR", cancellationToken);

    public Task<Guid> EnqueueAsync(
        AccountEmailKind kind,
        string? token,
        string? language,
        CancellationToken cancellationToken)
    {
        var message = new AccountEmailRequested(kind, "ana@example.com", Guid.CreateVersion7(), token, language);
        return EnqueueAsync(AccountEmailOutbox.Create(message, Time.GetUtcNow()), cancellationToken);
    }

    /// <summary>Grava o evento como o web gravaria, para o worker deste harness consumir.</summary>
    public async Task<Guid> EnqueueAsync(OutboxEvent outboxEvent, CancellationToken cancellationToken)
    {
        await using var dbContext = PostgresFixture.CreateDbContext(ConnectionString);
        dbContext.Add(outboxEvent);
        await dbContext.SaveChangesAsync(cancellationToken);
        return outboxEvent.Deliveries.Single().Id;
    }

    /// <summary>Tira um campo do payload, como um evento gravado antes de o campo existir.</summary>
    public async Task RemovePayloadFieldAsync(Guid deliveryId, string field, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            update ops.outbox_events e set payload = e.payload - $2
            from ops.outbox_deliveries d
            where d.outbox_event_id = e.id and d.id = $1
            """,
            connection);
        command.Parameters.AddWithValue(deliveryId);
        command.Parameters.AddWithValue(field);
        Assert.Equal(1, await command.ExecuteNonQueryAsync(cancellationToken));
    }

    public async Task<DeliveryRow> ReadDeliveryAsync(Guid deliveryId, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select d.status, d.attempts, d.fencing_token, d.available_at, d.last_error, e.payload::text, d.lease_expires_at
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
            reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6));
    }

    public string[] MailFiles() => Directory.GetFiles(MailDirectory.FullName, "*.eml");

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts)
        {
            host.Dispose();
        }

        await Task.Yield();
        MailDirectory.Delete(recursive: true);
    }
}

internal sealed record DeliveryRow(
    string Status,
    int Attempts,
    long FencingToken,
    DateTimeOffset AvailableAt,
    string? LastError,
    string Payload,
    DateTimeOffset? LeaseExpiresAt = null);
