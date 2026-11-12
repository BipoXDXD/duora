using System.Security.Cryptography;
using System.Text;
using Duora.Core.Safety;
using Duora.Infrastructure.Safety;
using Duora.IntegrationTests.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Duora.IntegrationTests.Safety;

/// <summary>
/// Caracterização da gravação da suspensão (#299, ADR-0042) nos pontos em que a transação decide depois de esperar por
/// um lock: a autorização ao vivo vem antes do replay, e uma gravação concorrente da mesma chave ou da mesma conta só
/// é reconhecida na inserção. Cada espera é determinística, por <c>pg_blocking_pids</c>, sem sleep nem hora real.
/// </summary>
[Trait("Acceptance", "T-10")]
public sealed class AccountSuspensionRecordingTests : IAsyncDisposable
{
    private const string InternalReason = "Motivo caracterizado.";
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(5);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
    private readonly DuoraWebFactory _factory;
    private readonly AccountSuspensionScenario _scenario;
    private readonly string _connectionString;

    public AccountSuspensionRecordingTests(PostgresFixture postgres)
    {
        _connectionString = postgres.ConnectionString;
        _factory = new DuoraWebFactory(postgres.ConnectionString) { Time = _time };
        _scenario = new AccountSuspensionScenario(postgres.ConnectionString, _factory, _time);
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    public static TheoryData<string> Revocations =>
    [
        "delete from identity.user_roles where user_id=$1 and role_id=(select id from identity.roles where name='moderator') and $2=$2",
        "update identity.login_sessions set revoked_at=now() where id=$2 and $1=$1",
        "update identity.login_sessions set totp_verified_at=null where id=$2 and $1=$1",
    ];

    /// <summary>A mesma chave já gravou a medida: se a permissão cai enquanto o pedido espera as guardas, vence a recusa, não o replay.</summary>
    [Theory]
    [MemberData(nameof(Revocations))]
    public async Task ARevocationWhileWaitingForTheGuardsIsRefusedBeforeTheReplayAndWritesNothing(string revocation)
    {
        var participant = await _scenario.CreateEligibleParticipantAsync();
        var (admin, browser) = await _scenario.PrivilegedBrowserAsync();
        using var __ = browser;
        var session = new ModeratorSession(admin.UserId, await MfaScenario.ReadSessionIdAsync(browser));
        var order = ModeratorOrder(admin.UserId, participant.UserId);
        var key = ModerationCommandKey.ForModerator(admin.UserId, AccountSuspensionScenario.NewKey());
        Assert.IsType<SuspensionOutcome.Suspended>(await SuspendAsync(session, order, key));
        await using var holder = await HoldAsync(
            "insert into safety.user_guards (user_id) values ($1) on conflict do nothing",
            "select 1 from safety.user_guards where user_id = $1 for update",
            participant.UserId);

        var repeating = SuspendAsync(session, order, key);
        await WaitUntilBlockedByAsync(holder.ProcessId);
        await _scenario.ExecuteAsync(revocation, admin.UserId, session.SessionId);
        await holder.DisposeAsync();

        Assert.IsType<SuspensionOutcome.Forbidden>(await repeating);
        await AssertOnlyTheFirstMeasureAsync(participant.UserId, "Suspended");
    }

    /// <summary>A mesma chave e o mesmo pedido gravados por outra transação depois do replay: a inserção recua e devolve aquela medida.</summary>
    [Fact]
    public async Task ARecordWithTheSameKeyAndRequestCommittedAfterTheReplayCheckIsReplayedAndWritesNothingElse()
    {
        var participant = await _scenario.CreateEligibleParticipantAsync();
        var (admin, browser) = await _scenario.PrivilegedBrowserAsync();
        using var __ = browser;
        var session = new ModeratorSession(admin.UserId, await MfaScenario.ReadSessionIdAsync(browser));
        var order = ModeratorOrder(admin.UserId, participant.UserId);
        var key = ModerationCommandKey.ForModerator(admin.UserId, AccountSuspensionScenario.NewKey());
        var recorded = Guid.NewGuid();
        await using var holder = await HoldAccountAsync(participant.UserId);

        var suspending = SuspendAsync(session, order, key);
        await WaitUntilBlockedByAsync(holder.ProcessId);
        await InsertMeasureAsync(recorded, participant.UserId, admin.UserId, key, IntentOf(participant.UserId));
        await holder.DisposeAsync();

        var outcome = Assert.IsType<SuspensionOutcome.Suspended>(await suspending);
        Assert.True(outcome.Replayed);
        Assert.Equal(recorded, outcome.View.Id);
        await AssertOnlyTheRecordedMeasureAsync(participant.UserId);
    }

    /// <summary>Outra medida ativa da conta, de outra chave, gravada depois do replay: a inserção recua e a conta já está suspensa.</summary>
    [Fact]
    public async Task AnActiveMeasureWithAnotherKeyCommittedAfterTheReplayCheckLeavesTheAccountAlreadySuspendedAndWritesNothingElse()
    {
        var participant = await _scenario.CreateEligibleParticipantAsync();
        var (admin, browser) = await _scenario.PrivilegedBrowserAsync();
        using var __ = browser;
        var session = new ModeratorSession(admin.UserId, await MfaScenario.ReadSessionIdAsync(browser));
        var order = ModeratorOrder(admin.UserId, participant.UserId);
        var other = ModerationCommandKey.ForModerator(admin.UserId, AccountSuspensionScenario.NewKey());
        await using var holder = await HoldAccountAsync(participant.UserId);

        var suspending = SuspendAsync(
            session, order, ModerationCommandKey.ForModerator(admin.UserId, AccountSuspensionScenario.NewKey()));
        await WaitUntilBlockedByAsync(holder.ProcessId);
        await InsertMeasureAsync(Guid.NewGuid(), participant.UserId, admin.UserId, other, IntentOf(participant.UserId));
        await holder.DisposeAsync();

        Assert.IsType<SuspensionOutcome.AlreadySuspended>(await suspending);
        await AssertOnlyTheRecordedMeasureAsync(participant.UserId);
    }

    private static SuspensionOrder ModeratorOrder(Guid moderator, Guid subject) =>
        SuspensionOrder.ByModerator(moderator, subject, SuspensionReason.TermsViolation, InternalReason);

    /// <summary>
    /// Hash do pedido persistido junto da medida (conta, medida, motivo, autoria e texto). O formato é contrato: as
    /// chaves já gravadas continuam reconhecendo o replay.
    /// </summary>
    private static byte[] IntentOf(Guid subject) => SHA256.HashData(Encoding.UTF8.GetBytes(
        $"{subject:D}\nSuspension\n{SuspensionReason.TermsViolation}\nHuman\n{InternalReason}"));

    private async Task<SuspensionOutcome> SuspendAsync(ModeratorSession session, SuspensionOrder order, ModerationCommandKey key)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AccountSuspensions>()
            .SuspendAsModeratorAsync(session, order, key, Ct);
    }

    private Task InsertMeasureAsync(Guid id, Guid subject, Guid author, ModerationCommandKey key, byte[] intent) =>
        _scenario.ExecuteAsync(
            """
            insert into safety.moderation_actions (id, subject_user_id, measure, reason_code, internal_reason, author_kind,
              author_user_id, decided_at, command_key, command_intent, version)
            values ($1, $2, 'Suspension', 'TermsViolation', $3, 'Human', $4, now(), $5, $6, 1)
            """,
            id, subject, InternalReason, author, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key.Scope))), intent);

    private async Task AssertOnlyTheFirstMeasureAsync(Guid subject, string status)
    {
        Assert.Equal(status, await _scenario.TextAsync("select status from identity.users where id = $1", subject));
        Assert.Equal(1, await _scenario.CountAsync(
            "select count(*) from safety.moderation_actions where subject_user_id = $1", subject));
        Assert.Equal(1, await _scenario.CountAsync(
            "select count(*) from ops.audit_events where action = 'safety.account_suspended' and target_id in "
            + "(select id from safety.moderation_actions where subject_user_id = $1)", subject));
    }

    /// <summary>A medida gravada à parte é a única: a transação que recuou não mexeu na conta nem auditou nada.</summary>
    private async Task AssertOnlyTheRecordedMeasureAsync(Guid subject)
    {
        Assert.Equal("Active", await _scenario.TextAsync("select status from identity.users where id = $1", subject));
        Assert.Equal(1, await _scenario.CountAsync(
            "select count(*) from safety.moderation_actions where subject_user_id = $1", subject));
        Assert.Equal(0, await _scenario.CountAsync(
            "select count(*) from ops.audit_events where action = 'safety.account_suspended' and target_id in "
            + "(select id from safety.moderation_actions where subject_user_id = $1)", subject));
    }

    private Task<Holder> HoldAccountAsync(Guid userId) =>
        HoldAsync(null, "select 1 from identity.users where id = $1 for share", userId);

    /// <summary>Mantém, numa transação própria, o lock pedido até o descarte; <paramref name="prepare"/> roda antes, já confirmado.</summary>
    private async Task<Holder> HoldAsync(string? prepare, string lockSql, Guid userId)
    {
        if (prepare is not null)
        {
            await _scenario.ExecuteAsync(prepare, userId);
        }

        var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(Ct);
        var transaction = await connection.BeginTransactionAsync(Ct);
        await using var command = new NpgsqlCommand(lockSql, connection, transaction);
        command.Parameters.AddWithValue(userId);
        await command.ExecuteNonQueryAsync(Ct);
        return new Holder(connection, transaction);
    }

    private async Task WaitUntilBlockedByAsync(int holderProcessId)
    {
        await using var observer = new NpgsqlConnection(_connectionString);
        await observer.OpenAsync(Ct);
        var deadline = DateTimeOffset.UtcNow + WaitLimit;
        while (true)
        {
            await using var command = new NpgsqlCommand(
                "select exists (select 1 from pg_stat_activity where $1 = any(pg_blocking_pids(pid)))", observer);
            command.Parameters.AddWithValue(holderProcessId);
            if ((bool)(await command.ExecuteScalarAsync(Ct))!)
            {
                return;
            }

            Assert.True(DateTimeOffset.UtcNow < deadline, "O pedido não chegou a esperar pelo lock.");
            await Task.Delay(10, Ct);
        }
    }

    private sealed class Holder(NpgsqlConnection connection, NpgsqlTransaction transaction) : IAsyncDisposable
    {
        public int ProcessId => connection.ProcessID;

        public async ValueTask DisposeAsync()
        {
            await transaction.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
