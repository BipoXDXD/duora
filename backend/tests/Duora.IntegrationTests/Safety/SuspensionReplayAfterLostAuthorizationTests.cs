using System.Net;
using Duora.Core.Identity;
using Duora.Core.Safety;
using Duora.Infrastructure.Safety;
using Duora.IntegrationTests.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Duora.IntegrationTests.Safety;

/// <summary>
/// T-27 (INV-03, INV-06): repetir uma suspensão com a mesma <c>Idempotency-Key</c> depois de perder a autorização não
/// devolve a resposta guardada. A autorização atual prevalece sobre o replay, na fronteira e no serviço.
/// </summary>
[Trait("Acceptance", "T-27")]
public sealed class SuspensionReplayAfterLostAuthorizationTests : IAsyncDisposable
{
    /// <summary>O motivo padrão de <see cref="AccountSuspensionScenario.SuspendBody"/>: só a mesma intenção vira replay.</summary>
    private const string Reason = "Ofensas repetidas contra os Termos.";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
    private readonly DuoraWebFactory _factory;
    private readonly AccountSuspensionScenario _scenario;

    public SuspensionReplayAfterLostAuthorizationTests(PostgresFixture postgres)
    {
        _factory = new DuoraWebFactory(postgres.ConnectionString) { Time = _time };
        _scenario = new AccountSuspensionScenario(postgres.ConnectionString, _factory, _time);
    }

    public enum Loss
    {
        RoleRemoved,
        TotpNoLongerRecent,
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Theory]
    [InlineData(Loss.RoleRemoved, HttpStatusCode.Forbidden, "ACCOUNT_MODERATION_FORBIDDEN")]
    [InlineData(Loss.TotpNoLongerRecent, HttpStatusCode.Unauthorized, "REAUTHENTICATION_REQUIRED")]
    public async Task TheSameKeyAfterLosingAuthorizationIsRefusedWithoutTheStoredSuspension(
        Loss loss, HttpStatusCode expectedStatus, string expectedCode)
    {
        var participant = await _scenario.CreateEligibleParticipantAsync();
        var (account, moderator) = await _scenario.PrivilegedBrowserAsync();
        using var __ = moderator;
        var key = AccountSuspensionScenario.NewKey();
        var first = await AccountSuspensionScenario.SuspendAsync(moderator, participant.UserId, key);
        var suspensionId = (string)first["suspensionId"]!;
        await LoseAuthorizationAsync(loss, account, moderator);

        using var replay = await moderator.PostJsonAsync(AccountSuspensionScenario.SuspendPath(participant.UserId),
            AccountSuspensionScenario.SuspendBody(Reason), AccountSuspensionScenario.WithKey(key));

        var body = (await ProblemAssert.IsProblemAsync(replay, expectedStatus, expectedCode)).ToJsonString();
        Assert.Null(replay.Headers.ETag);
        Assert.Null(replay.Headers.Location);
        var headers = string.Join('\n', replay.Headers.Concat(replay.Content.Headers)
            .Select(header => $"{header.Key}: {string.Join(',', header.Value)}"));
        foreach (var id in new[] { Guid.Parse(suspensionId), participant.UserId })
        {
            foreach (var text in new[] { id.ToString("D"), id.ToString("N") })
            {
                Assert.DoesNotContain(text, body, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(text, headers, StringComparison.OrdinalIgnoreCase);
            }
        }

        Assert.Equal(1, await _scenario.CountAsync(
            "select count(*) from safety.moderation_actions where subject_user_id = $1", participant.UserId));
        Assert.Equal(1, await _scenario.CountAsync(
            "select count(*) from ops.audit_events where action = 'safety.account_suspended' and target_id = $1",
            Guid.Parse(suspensionId)));
    }

    /// <summary>
    /// Sem a policy e sem o filtro de MFA da fronteira, o serviço ainda confere o papel e o TOTP recente antes de
    /// procurar a chave: a mesma ordem e a mesma chave dão <c>Forbidden</c>, nunca o <c>Suspended</c> com
    /// <c>Replayed</c>.
    /// </summary>
    [Theory]
    [InlineData(Loss.RoleRemoved)]
    [InlineData(Loss.TotpNoLongerRecent)]
    public async Task TheServiceRefusesTheReplayAfterLosingAuthorizationBeforeLookingUpTheKey(Loss loss)
    {
        var participant = await _scenario.CreateEligibleParticipantAsync();
        var (account, moderator) = await _scenario.PrivilegedBrowserAsync();
        using var __ = moderator;
        var key = AccountSuspensionScenario.NewKey();
        await AccountSuspensionScenario.SuspendAsync(moderator, participant.UserId, key);
        var session = new ModeratorSession(account.UserId, await MfaScenario.ReadSessionIdAsync(moderator));
        await LoseAuthorizationAsync(loss, account, moderator);

        await using var scope = _factory.Services.CreateAsyncScope();
        var outcome = await scope.ServiceProvider.GetRequiredService<AccountSuspensions>().SuspendAsModeratorAsync(
            session,
            SuspensionOrder.ByModerator(account.UserId, participant.UserId, SuspensionReason.TermsViolation, Reason),
            ModerationCommandKey.ForModerator(account.UserId, key),
            Ct);

        Assert.IsType<SuspensionOutcome.Forbidden>(outcome);
    }

    private async Task LoseAuthorizationAsync(Loss loss, TestAccount account, Browser moderator)
    {
        switch (loss)
        {
            case Loss.RoleRemoved:
                await _scenario.ExecuteAsync("delete from identity.user_roles where user_id = $1", account.UserId);
                await _scenario.Identity.GrantRoleAsync(account.UserId, AccountRoles.ProductAdmin);
                break;
            case Loss.TotpNoLongerRecent:
                await StepUpClock.ExpireRecentAuthenticationAsync(moderator, _time);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(loss), loss, "Perda de autorização sem cenário.");
        }
    }
}
