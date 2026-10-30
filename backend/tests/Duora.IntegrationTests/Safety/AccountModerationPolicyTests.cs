using System.Globalization;
using System.Security.Claims;
using Duora.Core.Identity;
using Duora.Web.Identity;
using Duora.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Duora.IntegrationTests.Safety;

/// <summary>
/// Policy <c>account-moderator</c> (ADR-0042) com o banco fora do ar: sem como ler o papel, falha fechada como
/// indisponibilidade (503 com <c>Retry-After</c>), nunca como proibição.
/// </summary>
public sealed class AccountModerationPolicyTests(PostgresFixture postgres)
{
    [Fact]
    public async Task AnUnreachableDatabaseIsTemporarilyUnavailableNotForbidden()
    {
        var dead = new Npgsql.NpgsqlConnectionStringBuilder(postgres.ConnectionString) { Port = 1, Timeout = 1 }.ConnectionString;
        await using var factory = new DuoraWebFactory(dead);
        await using var scope = factory.Services.CreateAsyncScope();
        var authorization = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();
        var policies = scope.ServiceProvider.GetRequiredService<IAuthorizationPolicyProvider>();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Response.Body = new MemoryStream();

        var result = await authorization.AuthorizeAsync(AdministrativePrincipal(), AccountModerationPolicy.Name);
        await scope.ServiceProvider.GetRequiredService<IAuthorizationMiddlewareResultHandler>().HandleAsync(
            _ => Task.CompletedTask,
            context,
            (await policies.GetPolicyAsync(AccountModerationPolicy.Name))!,
            PolicyAuthorizationResult.Forbid(result.Failure));

        Assert.False(result.Succeeded);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.True(int.Parse(context.Response.Headers.RetryAfter.ToString(), CultureInfo.InvariantCulture) > 0);
        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync(TestContext.Current.CancellationToken);
        Assert.Contains("TEMPORARILY_UNAVAILABLE", body, StringComparison.Ordinal);
    }

    /// <summary>Principal como o ticket de uma sessão administrativa com MFA o monta (<see cref="DuoraClaims"/>).</summary>
    private static ClaimsPrincipal AdministrativePrincipal()
    {
        var mfaAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        return new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, Guid.CreateVersion7().ToString("D")),
                new Claim(DuoraClaims.SessionId, Guid.CreateVersion7().ToString("D")),
                new Claim(DuoraClaims.Policy, nameof(SessionPolicy.Administrative)),
                new Claim(DuoraClaims.AccountIsPrivileged, bool.TrueString),
                new Claim(DuoraClaims.MfaVerifiedAt, mfaAt),
            ],
            DuoraAuthentication.Scheme));
    }
}
