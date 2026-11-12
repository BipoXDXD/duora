using System.Net;
using Duora.Core.Identity;
using Duora.IntegrationTests.Identity;
using Microsoft.Extensions.Time.Testing;

namespace Duora.IntegrationTests.Safety;

/// <summary>
/// Revogação de suspensão (issue #299, ADR-0042) que não acha o que revogar: conta inexistente ou privilegiada e
/// suspensão que não é daquela conta respondem o mesmo 404, sem gravar nada. Revogar uma suspensão já revogada não é
/// 404: a medida existe, e a resposta é 412 (coberto em <c>AccountSuspensionTests</c>).
/// </summary>
[Trait("Acceptance", "T-10")]
public sealed class AccountSuspensionLiftNotFoundTests : IAsyncDisposable
{
    private const string Lifted = "safety.account_suspension_lifted";

    private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
    private readonly DuoraWebFactory _factory;
    private readonly AccountSuspensionScenario _scenario;

    public AccountSuspensionLiftNotFoundTests(PostgresFixture postgres)
    {
        _factory = new DuoraWebFactory(postgres.ConnectionString) { Time = _time };
        _scenario = new AccountSuspensionScenario(postgres.ConnectionString, _factory, _time);
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task LiftingForAnAccountThatDoesNotExistIsNotFoundWithoutItsId()
    {
        var (_, moderator) = await _scenario.PrivilegedBrowserAsync();
        using var __ = moderator;
        var absent = Guid.NewGuid();
        var suspensionId = Guid.NewGuid();

        using var response = await LiftAsync(moderator, absent, suspensionId, Tag(suspensionId));

        var body = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.NotFound, "NOT_FOUND");
        Assert.DoesNotContain(absent.ToString(), body.ToJsonString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await _scenario.CountAsync("select count(*) from safety.user_guards where user_id = $1", absent));
        await AssertNotAuditedAsync(suspensionId);
    }

    [Fact]
    public async Task LiftingAnUnknownSuspensionOfAnExistingAccountIsNotFoundAndKeepsTheRealOneActive()
    {
        var participant = await _scenario.CreateEligibleParticipantAsync();
        var (_, moderator) = await _scenario.PrivilegedBrowserAsync();
        using var __ = moderator;
        var suspension = await AccountSuspensionScenario.SuspendAsync(moderator, participant.UserId);
        var realId = Guid.Parse((string)suspension["suspensionId"]!);
        var unknown = Guid.NewGuid();

        using var response = await LiftAsync(moderator, participant.UserId, unknown, Tag(unknown));

        var body = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.NotFound, "NOT_FOUND");
        Assert.DoesNotContain(unknown.ToString(), body.ToJsonString(), StringComparison.OrdinalIgnoreCase);
        await AssertStillSuspendedAsync(participant.UserId, realId);
        await AssertNotAuditedAsync(unknown);
        await AssertNotAuditedAsync(realId);

        using var real = await LiftAsync(moderator, participant.UserId, realId, (string)suspension["eTag"]!);
        Assert.Equal(HttpStatusCode.OK, real.StatusCode);
    }

    [Fact]
    public async Task LiftingTheSuspensionOfAnotherAccountThroughThisAccountsPathIsNotFoundAndChangesNeither()
    {
        var first = await _scenario.CreateEligibleParticipantAsync();
        var second = await _scenario.CreateEligibleParticipantAsync();
        var (_, moderator) = await _scenario.PrivilegedBrowserAsync();
        using var __ = moderator;
        var ofFirst = await AccountSuspensionScenario.SuspendAsync(moderator, first.UserId);
        var ofSecond = await AccountSuspensionScenario.SuspendAsync(moderator, second.UserId);
        var secondId = Guid.Parse((string)ofSecond["suspensionId"]!);

        using var response = await LiftAsync(moderator, first.UserId, secondId, (string)ofSecond["eTag"]!);

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.NotFound, "NOT_FOUND");
        await AssertStillSuspendedAsync(first.UserId, Guid.Parse((string)ofFirst["suspensionId"]!));
        await AssertStillSuspendedAsync(second.UserId, secondId);
        await AssertNotAuditedAsync(secondId);
    }

    [Fact]
    public async Task LiftingTheSuspensionOfAnAccountThatBecamePrivilegedIsNotFoundAndKeepsItActive()
    {
        var participant = await _scenario.CreateEligibleParticipantAsync();
        var (_, moderator) = await _scenario.PrivilegedBrowserAsync();
        using var __ = moderator;
        var suspension = await AccountSuspensionScenario.SuspendAsync(moderator, participant.UserId);
        var suspensionId = Guid.Parse((string)suspension["suspensionId"]!);
        await _scenario.ExecuteAsync(
            "delete from identity.user_roles where user_id = $1 and role_id = (select id from identity.roles where name = $2)",
            participant.UserId, AccountRoles.Participant);
        await _scenario.ExecuteAsync(
            "insert into identity.user_roles (user_id, role_id) select $1, id from identity.roles where name = $2",
            participant.UserId, AccountRoles.Moderator);

        using var response = await LiftAsync(moderator, participant.UserId, suspensionId, (string)suspension["eTag"]!);

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.NotFound, "NOT_FOUND");
        await AssertStillSuspendedAsync(participant.UserId, suspensionId);
        await AssertNotAuditedAsync(suspensionId);
    }

    /// <summary>Etiqueta bem formada para uma suspensão que o cliente imagina existir, na versão 1.</summary>
    private static string Tag(Guid suspensionId) => $"\"suspension-{suspensionId:N}-1\"";

    private static Task<HttpResponseMessage> LiftAsync(Browser moderator, Guid userId, Guid suspensionId, string eTag) =>
        moderator.PostJsonAsync(
            AccountSuspensionScenario.LiftPath(userId, suspensionId),
            new { internalReason = "Revogação que não encontra a medida." },
            AccountSuspensionScenario.WithIfMatch(eTag));

    private async Task AssertNotAuditedAsync(Guid suspensionId) =>
        Assert.Equal(0, await _scenario.CountAsync(
            "select count(*) from ops.audit_events where action = $1 and target_id = $2", Lifted, suspensionId));

    private async Task AssertStillSuspendedAsync(Guid userId, Guid suspensionId)
    {
        Assert.Equal("Suspended", await _scenario.TextAsync("select status from identity.users where id = $1", userId));
        Assert.Equal(1, await _scenario.CountAsync(
            "select count(*) from safety.moderation_actions where id = $1 and lifted_at is null and version = 1", suspensionId));
    }
}
