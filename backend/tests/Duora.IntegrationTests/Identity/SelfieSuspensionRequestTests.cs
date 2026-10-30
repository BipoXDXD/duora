using System.Diagnostics.Metrics;
using System.Net;
using Duora.IntegrationTests.Safety;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using static Duora.IntegrationTests.Identity.SelfieFailureLockoutHarness;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// Pedido de suspensão da décima reprovação (issue #300, ADR-0043) no PostgreSQL real: espera outra medida ativa
/// sair, desiste se a contagem acabou, e falha determinística tem teto e termina morta, com log e métrica.
/// </summary>
public sealed class SelfieSuspensionRequestTests(PostgresFixture postgres)
{
    private const string Consumer = "selfie-attempts-suspension";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

    [Fact]
    public async Task AnotherSuspensionBeforeTheConsumerIsWaitedForAndTheSelfieSuspensionFollowsTheLift()
    {
        var (lockout, connection) = await NewAsync();
        await using var host = lockout.Host();
        var suspensions = new AccountSuspensionScenario(connection, host, _time);
        var scenario = suspensions.Identity;
        var account = await scenario.CreateConfirmedAccountAsync();
        await lockout.ExhaustAsync(host, account.UserId);
        var (_, moderator) = await suspensions.PrivilegedBrowserAsync();
        using var __ = moderator;
        var terms = await AccountSuspensionScenario.SuspendAsync(moderator, account.UserId);

        Assert.Equal(1, await SuspendPendingAsync(host));
        var waiting = await DeliveryAsync(scenario);
        using (var lifted = await moderator.PostJsonAsync(
            AccountSuspensionScenario.LiftPath(account.UserId, Guid.Parse((string)terms["suspensionId"]!)),
            new { internalReason = "Revisto." },
            AccountSuspensionScenario.WithIfMatch((string)terms["eTag"]!)))
        {
            Assert.Equal(HttpStatusCode.OK, lifted.StatusCode);
        }

        _time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(1, await SuspendPendingAsync(host));

        Assert.Equal(("Pending", 0, "AwaitingOtherMeasure"), (waiting.Status, waiting.Attempts, waiting.LastError));
        Assert.Equal("Suspended", await scenario.ScalarTextAsync("select status from identity.users where id = $1", account.UserId));
        Assert.Equal(1, await scenario.ScalarAsync("""
            select count(*) from safety.moderation_actions
            where subject_user_id = $1 and reason_code = 'SelfieVerificationFailed' and lifted_at is null
            """, account.UserId));
        Assert.Equal("Delivered", (await DeliveryAsync(scenario)).Status);
    }

    [Fact]
    public async Task WhileAnotherMeasureLastsTheWaitGrowsWithTheAgeOfTheRequestUpToOneHour()
    {
        var (lockout, connection) = await NewAsync();
        await using var host = lockout.Host();
        var suspensions = new AccountSuspensionScenario(connection, host, _time);
        var scenario = suspensions.Identity;
        var account = await scenario.CreateConfirmedAccountAsync();
        await lockout.ExhaustAsync(host, account.UserId);
        var (_, moderator) = await suspensions.PrivilegedBrowserAsync();
        using var __ = moderator;
        await AccountSuspensionScenario.SuspendAsync(moderator, account.UserId);
        var requestedAt = DateTimeOffset.Parse(await scenario.ScalarTextAsync(
            "select to_char(occurred_at at time zone 'UTC', 'YYYY-MM-DD\"T\"HH24:MI:SS.US\"Z\"') from ops.outbox_events where type = 'identity.selfie-attempts-exhausted'"),
            System.Globalization.CultureInfo.InvariantCulture);

        _time.SetUtcNow(requestedAt + TimeSpan.FromMinutes(10));
        await SuspendPendingAsync(host);
        var young = await DeliveryAsync(scenario);
        _time.SetUtcNow(requestedAt + TimeSpan.FromDays(3));
        await SuspendPendingAsync(host);
        var capped = await DeliveryAsync(scenario);

        Assert.Equal(requestedAt + TimeSpan.FromMinutes(20), young.AvailableAt);
        Assert.Equal(requestedAt + TimeSpan.FromDays(3) + TimeSpan.FromHours(1), capped.AvailableAt);
        Assert.Equal(0, capped.Attempts);
    }

    [Fact]
    public async Task ARequestWhoseCountEndedWhileWaitingFinishesWithoutSuspending()
    {
        var (lockout, connection) = await NewAsync();
        await using var host = lockout.Host();
        var suspensions = new AccountSuspensionScenario(connection, host, _time);
        var scenario = suspensions.Identity;
        var account = await scenario.CreateConfirmedAccountAsync();
        await lockout.ExhaustAsync(host, account.UserId);
        var (_, moderator) = await suspensions.PrivilegedBrowserAsync();
        using var __ = moderator;
        var terms = await AccountSuspensionScenario.SuspendAsync(moderator, account.UserId);
        await SuspendPendingAsync(host);
        await scenario.ScalarAsync("delete from identity.selfie_failures where user_id = $1 returning 1", account.UserId);
        using (var lifted = await moderator.PostJsonAsync(
            AccountSuspensionScenario.LiftPath(account.UserId, Guid.Parse((string)terms["suspensionId"]!)),
            new { internalReason = "Revisto." },
            AccountSuspensionScenario.WithIfMatch((string)terms["eTag"]!)))
        {
            Assert.Equal(HttpStatusCode.OK, lifted.StatusCode);
        }

        _time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(1, await SuspendPendingAsync(host));

        Assert.Equal(("Delivered", "NoLongerExhausted"), ((await DeliveryAsync(scenario)).Status, (await DeliveryAsync(scenario)).LastError));
        Assert.Equal("PendingEligibility", await scenario.ScalarTextAsync("select status from identity.users where id = $1", account.UserId));
        Assert.Equal(0, await scenario.ScalarAsync(
            "select count(*) from safety.moderation_actions where subject_user_id = $1 and reason_code = 'SelfieVerificationFailed'",
            account.UserId));
    }

    [Fact]
    public async Task ADeterministicFailureIsRetriedUpToTheCeilingThenMarkedDeadWithoutStoppingTheOthers()
    {
        var connection = await postgres.CreateMigratedDatabaseAsync(Ct);
        var lockout = new SelfieFailureLockoutHarness(connection, _time, new ScriptedSelfieProvider(_time));
        var logs = new CapturingLoggerProvider();
        await using var host = lockout.Host(logs: logs);
        var scenario = new IdentityScenario(connection, host);
        var poisoned = await scenario.CreateConfirmedAccountAsync();
        var healthy = await scenario.CreateConfirmedAccountAsync();
        await lockout.ExhaustAsync(host, poisoned.UserId);
        await lockout.ExhaustAsync(host, healthy.UserId);
        await RefuseSuspensionsOfAsync(connection, poisoned.UserId);
        var dead = new List<long>();
        using var listener = ListenToDeadRequests(dead);

        await SuspendPendingAsync(host);
        var afterFirst = await DeliveryOfAsync(scenario, poisoned.UserId);
        for (var attempt = 0; attempt < 12; attempt++)
        {
            _time.Advance(TimeSpan.FromMinutes(16));
            await SuspendPendingAsync(host);
        }

        Assert.Equal("Suspended", await scenario.ScalarTextAsync("select status from identity.users where id = $1", healthy.UserId));
        Assert.Equal(("Pending", 1, "PostgresException"), (afterFirst.Status, afterFirst.Attempts, afterFirst.LastError));
        var final = await DeliveryOfAsync(scenario, poisoned.UserId);
        Assert.Equal(("Failed", 10, "MaxAttemptsExceeded"), (final.Status, final.Attempts, final.LastError));
        Assert.Equal("PendingEligibility", await scenario.ScalarTextAsync("select status from identity.users where id = $1", poisoned.UserId));
        Assert.Equal([1L], dead);
        var error = Assert.Single(logs.Entries, entry => entry.Level == LogLevel.Error
            && entry.Category == typeof(Duora.Infrastructure.Identity.SelfieAttemptsSuspensionDispatcher).FullName);
        Assert.Contains("dead", error.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(poisoned.UserId.ToString(), error.Text, StringComparison.OrdinalIgnoreCase);
    }

    private static MeterListener ListenToDeadRequests(List<long> dead)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Name == "duora.identity.selfie_suspension_requests.dead")
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => dead.Add(value));
        listener.Start();
        return listener;
    }

    /// <summary>Falha determinística na gravação da medida, só para esta conta (não é contenção de lock).</summary>
    private async Task RefuseSuspensionsOfAsync(string connection, Guid userId)
    {
        var database = new NpgsqlConnectionStringBuilder(connection).Database;
        var migrator = new NpgsqlConnectionStringBuilder(postgres.MigratorConnectionString) { Database = database };
        await using var owner = new NpgsqlConnection(migrator.ConnectionString);
        await owner.OpenAsync(Ct);
        await using var command = new NpgsqlCommand($"""
            create function safety.test_refuse_suspension() returns trigger language plpgsql as $$
            begin
              if new.subject_user_id = '{userId:D}' then
                raise exception 'synthetic deterministic failure' using errcode = 'P0001';
              end if;
              return new;
            end $$;
            create trigger tg_test_refuse_suspension before insert on safety.moderation_actions
              for each row execute function safety.test_refuse_suspension();
            """, owner);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<Delivery> DeliveryAsync(IdentityScenario scenario) =>
        await ReadDeliveryAsync(scenario, "select id::text from ops.outbox_deliveries where consumer = $1", Consumer);

    private static async Task<Delivery> DeliveryOfAsync(IdentityScenario scenario, Guid userId) =>
        await ReadDeliveryAsync(scenario, """
            select d.id::text from ops.outbox_deliveries d join ops.outbox_events e on e.id = d.outbox_event_id
            where d.consumer = $1 and e.resource_id in (select verification_id from identity.selfie_failures where user_id = $2)
            """, Consumer, userId);

    private static async Task<Delivery> ReadDeliveryAsync(IdentityScenario scenario, string idSql, params object[] parameters)
    {
        var id = Guid.Parse(await scenario.ScalarTextAsync(idSql, parameters));
        return new Delivery(
            await scenario.ScalarTextAsync("select status from ops.outbox_deliveries where id = $1", id),
            (int)await scenario.ScalarAsync("select attempts::bigint from ops.outbox_deliveries where id = $1", id),
            await scenario.ScalarTextAsync("select coalesce(last_error, '') from ops.outbox_deliveries where id = $1", id),
            DateTimeOffset.Parse(
                await scenario.ScalarTextAsync("select to_char(available_at at time zone 'UTC', 'YYYY-MM-DD\"T\"HH24:MI:SS.US\"Z\"') from ops.outbox_deliveries where id = $1", id),
                System.Globalization.CultureInfo.InvariantCulture));
    }

    private async Task<(SelfieFailureLockoutHarness Lockout, string Connection)> NewAsync()
    {
        var connection = await postgres.CreateMigratedDatabaseAsync(Ct);
        return (new SelfieFailureLockoutHarness(connection, _time, new ScriptedSelfieProvider(_time)), connection);
    }

    private sealed record Delivery(string Status, int Attempts, string LastError, DateTimeOffset AvailableAt);
}
