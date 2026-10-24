using System.Text.Json;
using Duora.Core;
using Duora.Core.Identity;
using Duora.Core.Notifications;
using Duora.Infrastructure.Email;
using Duora.Infrastructure.Outbox;
using Duora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

// This executable is test-only. Its assembly is a friend of the pinned Infrastructure build.
// Producing uses that version's real AccountEmailOutbox, not a hand-written payload approximation.
var connectionString = Environment.GetEnvironmentVariable("DUORA_TEST_DB")
    ?? throw new InvalidOperationException("Missing isolated test database");
var builder = new DbContextOptionsBuilder<DuoraDbContext>();
builder.UseDuoraNpgsql(connectionString);
var options = builder.Options;
using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(3));
var token = cancellation.Token;
var mode = args.Length > 0 ? args[0] : throw new ArgumentException("Missing probe mode");
if (mode == "migrate")
{
    await using var context = new DuoraDbContext(options);
    await context.Database.MigrateAsync(token);
    Console.WriteLine("{\"migrated\":true}");
}
else if (mode == "produce")
{
    var kind = Enum.Parse<AccountEmailKind>(args[1]);
    const string recipient = "synthetic@example.invalid";
    var request = new AccountEmailRequested(kind, recipient, Guid.CreateVersion7(), null, "en");
    var recorded = AccountEmailOutbox.Create(request, DateTimeOffset.UtcNow);
    await using var context = new DuoraDbContext(options);
    context.Add(recorded);
    await context.SaveChangesAsync(token);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        event_id = recorded.Id,
        delivery_id = recorded.Deliveries.Single().Id,
        kind = args[1],
        producer_assembly_id = typeof(EmailServiceCollectionExtensions).Assembly.ManifestModule.ModuleVersionId,
    }));
}
else if (mode is "consume" or "claim")
{
    var pickup = Environment.GetEnvironmentVariable("DUORA_TEST_PICKUP")
        ?? throw new InvalidOperationException("Missing isolated pickup directory");
    var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Email:PickupDirectory"] = pickup,
        ["Outbox:BatchSize"] = "1",
    }).Build();
    if (!PublicOrigin.TryParse("https://duora-mail-test.invalid", out var origin)
        || !EmailAddress.TryParse("support@example.invalid", out var support))
    {
        throw new InvalidOperationException("Invalid synthetic addresses");
    }
    var services = new ServiceCollection();
    services.AddSingleton<IConfiguration>(configuration);
    services.AddLogging();
    services.AddSingleton(TimeProvider.System);
    services.AddSingleton(NpgsqlDataSource.Create(connectionString));
    services.AddSingleton(new AccountEmails(origin, support));
    services.AddTransactionalEmailDispatch();
    await using var provider = services.BuildServiceProvider();
    int claimed;
    if (mode == "claim")
    {
        var now = DateTimeOffset.UtcNow;
        var batch = await provider.GetRequiredService<OutboxDeliveryQueue>().ClaimWithAttemptLimitAsync(
            AccountEmailOutbox.Consumer, 1, now, now + TimeSpan.FromSeconds(2), 8, token);
        claimed = batch.Claimed.Count;
    }
    else
    {
        claimed = await provider.GetRequiredService<TransactionalEmailDispatcher>().DispatchBatchAsync(token);
    }
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        claimed,
        consumer_assembly_id = typeof(EmailServiceCollectionExtensions).Assembly.ManifestModule.ModuleVersionId,
    }));
}
else if (mode == "inspect")
{
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync(token);
    await using var command = new NpgsqlCommand("""
        select d.status, d.attempts, d.last_error, d.completed_at, e.payload::text
        from ops.outbox_deliveries d join ops.outbox_events e on e.id = d.outbox_event_id
        where d.id = $1
        """, connection);
    command.Parameters.AddWithValue(Guid.Parse(args[1]));
    await using var reader = await command.ExecuteReaderAsync(token);
    if (!await reader.ReadAsync(token))
    {
        throw new InvalidOperationException("Synthetic delivery not found");
    }
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        delivery_id = args[1],
        status = reader.GetString(0),
        attempts = reader.GetInt32(1),
        error_type = reader.IsDBNull(2) ? null : reader.GetString(2),
        completed_at = reader.IsDBNull(3) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(3),
        payload_cleared = reader.GetString(4) == "{}",
    }));
}
else if (mode == "guard")
{
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync(token);
    await using var command = new NpgsqlCommand("""
        select count(*) from ops.outbox_deliveries d
        join ops.outbox_events e on e.id = d.outbox_event_id
        where d.consumer = 'transactional-email'
          and d.status in ('Pending', 'InFlight')
          and e.payload->>'kind' = 'RecoveryCodeUsedNotice'
        """, connection);
    var count = (long)(await command.ExecuteScalarAsync(token) ?? throw new InvalidOperationException());
    Console.WriteLine(JsonSerializer.Serialize(new { pending_new_kind = count, rollback_allowed = count == 0 }));
}
else
{
    throw new ArgumentException("Unknown isolated probe mode");
}
