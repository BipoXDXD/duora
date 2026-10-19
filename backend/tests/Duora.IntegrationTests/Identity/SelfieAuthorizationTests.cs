using Duora.Infrastructure.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Duora.IntegrationTests.Identity;

public sealed class SelfieAuthorizationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task ParticipationRequiresEmailAndPresenceButNoAgeEvidence()
    {
        await using var factory = new DuoraWebFactory(postgres.ConnectionString);
        var scenario = new IdentityScenario(postgres, factory);
        var account = await scenario.CreateConfirmedAccountAsync();
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<SelfieVerificationService>();
        var ct = TestContext.Current.CancellationToken;
        Assert.False(await service.GetEligibilityAsync(account.UserId, ct));
        await scenario.ScalarAsync("update identity.users set status = 'Active' where id = $1 returning 1", account.UserId);
        Assert.False(await service.GetEligibilityAsync(account.UserId, ct));
        await scenario.ScalarAsync("insert into identity.selfie_verifications (id,user_id,state,policy_version,requested_at,expires_at,version) values ($1,$2,'Passed','test',now(),now(),0) returning 1", Guid.NewGuid(), account.UserId);
        Assert.True(await service.GetEligibilityAsync(account.UserId, ct));
        await scenario.ScalarAsync("update identity.users set status = 'Suspended' where id = $1 returning 1", account.UserId);
        Assert.False(await service.GetEligibilityAsync(account.UserId, ct));
        await scenario.ScalarAsync("update identity.users set status = 'Active', email_confirmed = false where id = $1 returning 1", account.UserId);
        Assert.False(await service.GetEligibilityAsync(account.UserId, ct));
    }
}
