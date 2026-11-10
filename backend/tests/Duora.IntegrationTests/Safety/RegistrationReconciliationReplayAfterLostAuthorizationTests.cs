using System.Net;
using Duora.Core.Identity;
using Duora.Infrastructure.Operations;
using Duora.IntegrationTests.Identity;

namespace Duora.IntegrationTests.Safety;

/// <summary>
/// T-27 (INV-03, INV-06) na reconciliação de inscrições (API-ADM26 e API-ADM28): criar o run e aplicar a unidade de novo,
/// com a mesma <c>Idempotency-Key</c> e o mesmo corpo, depois de o moderador perder o papel ou o TOTP recente, não devolve
/// o run nem o recibo guardados e não grava nada.
/// </summary>
[Trait("Acceptance", "T-27")]
public sealed class RegistrationReconciliationReplayAfterLostAuthorizationTests(PostgresFixture postgres)
{
    public enum Loss
    {
        RoleRemoved,
        TotpNoLongerRecent,
    }

    [Theory]
    [InlineData(Loss.RoleRemoved, HttpStatusCode.Forbidden, "ACCOUNT_MODERATION_FORBIDDEN")]
    [InlineData(Loss.TotpNoLongerRecent, HttpStatusCode.Unauthorized, "REAUTHENTICATION_REQUIRED")]
    public async Task TheSameRunKeyAfterLosingAuthorizationIsRefusedWithoutTheStoredRun(
        Loss loss, HttpStatusCode expectedStatus, string expectedCode)
    {
        await using var a = await ReconciliationApplyScenario.CreateAsync(postgres);
        await LoseAuthorizationAsync(a, loss);
        var before = await a.FingerprintAsync();

        using var replay = await a.Moderator.PostJsonAsync(ReconciliationApplyScenario.Path, a.RunBody,
            AccountSuspensionScenario.WithKey(a.RunKey));

        var body = (await ProblemAssert.IsProblemAsync(replay, expectedStatus, expectedCode)).ToJsonString();
        Assert.DoesNotContain(a.Run.ToString(), body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(a.Run.ToString("N"), body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(a.Unit.ToString(), body, StringComparison.OrdinalIgnoreCase);
        Assert.False(replay.Headers.Contains("Location"));
        Assert.Null(replay.Headers.ETag);
        Assert.Equal(before, await a.FingerprintAsync());
        Assert.Equal(1, await a.S.CountAsync("select count(*) from ops.registration_reconciliation_runs"));
        Assert.Equal(1, await a.S.CountAsync(
            "select count(*) from ops.audit_events where action='ops.registration_reconciliation_run_created'"));
    }

    [Theory]
    [InlineData(Loss.RoleRemoved, HttpStatusCode.Forbidden, "ACCOUNT_MODERATION_FORBIDDEN")]
    [InlineData(Loss.TotpNoLongerRecent, HttpStatusCode.Unauthorized, "REAUTHENTICATION_REQUIRED")]
    public async Task TheSameApplyKeyAfterLosingAuthorizationIsRefusedWithoutTheStoredReceipt(
        Loss loss, HttpStatusCode expectedStatus, string expectedCode)
    {
        await using var a = await ReconciliationApplyScenario.CreateAsync(postgres);
        var key = AccountSuspensionScenario.NewKey();
        using var first = await a.PostApplyAsync(key: key);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var receipt = await MfaScenario.ReadObjectAsync(first);
        var correlation = (string)receipt["correlationId"]!;
        var facts = receipt["factIds"]!.AsArray().Select(f => (string)f!).ToArray();
        await LoseAuthorizationAsync(a, loss);
        var before = await a.FingerprintAsync();

        using var replay = await a.PostApplyAsync(key: key);

        var body = (await ProblemAssert.IsProblemAsync(replay, expectedStatus, expectedCode)).ToJsonString();
        foreach (var secret in facts.Append(correlation).Append(a.Run.ToString()).Append(a.Unit.ToString()))
        {
            Assert.DoesNotContain(secret, body, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(before, await a.FingerprintAsync());
        Assert.Equal(facts.Length, await a.S.CountAsync("select count(*) from ops.registration_reconciliation_unit_facts"));
        Assert.Equal(1, await a.S.CountAsync(
            "select count(*) from ops.audit_events where action='events.legacy_registration_reconciliation_applied'"));
    }

    /// <summary>
    /// Sem a policy da fronteira, o coordenador ainda confere a moderação antes de devolver o recibo da unidade terminal:
    /// a mesma chave de um ex-moderador dá <c>Forbidden</c>, nunca o <c>Receipt</c> do replay.
    /// </summary>
    [Fact]
    public async Task TheCoordinatorRefusesTheReceiptToAFormerModeratorBeforeReadingIt()
    {
        await using var a = await ReconciliationApplyScenario.CreateAsync(postgres);
        var key = AccountSuspensionScenario.NewKey();
        Assert.IsType<ReconciliationApplyOutcome.Receipt>(await a.ApplyAsync(key: key, session: a.Session));
        await LoseAuthorizationAsync(a, Loss.RoleRemoved);

        var replay = await a.ApplyAsync(key: key, session: a.Session);

        Assert.IsType<ReconciliationApplyOutcome.Forbidden>(replay);
        Assert.Equal(1, await a.S.CountAsync(
            "select count(*) from ops.audit_events where action='events.legacy_registration_reconciliation_applied'"));
    }

    private static async Task LoseAuthorizationAsync(ReconciliationApplyScenario a, Loss loss)
    {
        switch (loss)
        {
            case Loss.RoleRemoved:
                await a.Accounts.ExecuteAsync("delete from identity.user_roles where user_id = $1", a.ModeratorId);
                await a.Accounts.Identity.GrantRoleAsync(a.ModeratorId, AccountRoles.ProductAdmin);
                break;
            case Loss.TotpNoLongerRecent:
                await StepUpClock.ExpireRecentAuthenticationAsync(a.Moderator, a.S.Clock);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(loss), loss, "Perda de autorização sem cenário.");
        }
    }
}
