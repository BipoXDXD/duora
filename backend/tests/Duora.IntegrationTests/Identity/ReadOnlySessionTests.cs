using Duora.Core.Identity;
using Duora.Infrastructure.Identity;
using Duora.Web.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// Leitura de sessão sem escrita (issue #321, ADR-0048): <see cref="LoginSessions.ReadValidAsync"/> aplica as mesmas
/// validações do toque sem UPDATE, e o ticket store só a usa na rota marcada com <see cref="ReadOnlySessionEndpoint"/>.
/// Nessa rota não há toque, renovação, gravação nem revogação de sessão.
/// </summary>
public sealed class ReadOnlySessionTests : IAsyncDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
    private readonly DuoraWebFactory _factory;
    private readonly IdentityScenario _scenario;

    public ReadOnlySessionTests(PostgresFixture postgres)
    {
        _factory = new DuoraWebFactory(postgres.ConnectionString) { Time = _time };
        _scenario = new IdentityScenario(postgres, _factory);
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private LoginSessions Sessions => _factory.Services.GetRequiredService<LoginSessions>();

    private ITicketStore Store => _factory.Services.GetRequiredService<ITicketStore>();

    /// <summary>Lista fechada: abrir mão do toque é exceção do ADR-0048, nunca padrão de rota nova.</summary>
    [Fact]
    public void OnlyTheReconciliationPreviewReadsTheSessionWithoutTouching()
    {
        var routes = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata.GetMetadata<ReadOnlySessionEndpoint>() is not null)
            .SelectMany(endpoint => (endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["*"])
                .Select(method => $"{method} /{endpoint.RoutePattern.RawText?.Trim('/')}"));

        Assert.Equal(["GET /api/v1/admin/registration-reconciliations/preview"], routes);
    }

    [Fact]
    public async Task ReadingAValidSessionReturnsWhatTheTouchReturnsAndWritesNothing()
    {
        var (userId, key) = await OpenSessionAsync();
        _time.Advance(TimeSpan.FromMinutes(1));
        var before = await RowAsync(userId);

        var read = await Sessions.ReadValidAsync(key, Ct);

        Assert.Equal(before, await RowAsync(userId));
        var touched = await Sessions.TouchAsync(key, Ct);
        Assert.NotNull(read);
        Assert.NotNull(touched);
        Assert.Equal(read.ProtectedTicket, touched.ProtectedTicket);
        Assert.Equal(read with { ProtectedTicket = Array.Empty<byte>() }, touched with { ProtectedTicket = Array.Empty<byte>() });
        Assert.NotEqual(before, await RowAsync(userId));
    }

    [Theory]
    [InlineData("revoked")]
    [InlineData("idle-expired")]
    [InlineData("absolute-expired")]
    [InlineData("security-stamp-changed")]
    [InlineData("temporary-password-expired")]
    [InlineData("unknown-key")]
    public async Task AnInvalidSessionIsRefusedByTheReadExactlyAsByTheTouch(string invalidation)
    {
        var (userId, key) = await OpenSessionAsync();
        key = await InvalidateAsync(invalidation, userId, key);
        var before = await RowAsync(userId);

        var read = await Sessions.ReadValidAsync(key, Ct);

        Assert.Null(read);
        Assert.Equal(before, await RowAsync(userId));
        Assert.Null(await Sessions.TouchAsync(key, Ct));
    }

    [Fact]
    public async Task OnAReadOnlyRouteTheTicketStoreRetrievesWithoutTouching()
    {
        var (userId, key) = await OpenSessionAsync();
        _time.Advance(TimeSpan.FromMinutes(1));
        var before = await RowAsync(userId);

        var ticket = await Store.RetrieveAsync(key, ReadOnlyRoute(), Ct);

        Assert.NotNull(ticket);
        Assert.Equal(userId, DuoraClaims.ReadUserId(ticket.Principal));
        Assert.Equal(SessionPolicy.Normal, DuoraClaims.ReadSession(ticket.Principal)?.Policy);
        Assert.Equal(before, await RowAsync(userId));
    }

    [Fact]
    public async Task OnAnyOtherRouteTheTicketStoreKeepsSlidingTheSession()
    {
        var (userId, key) = await OpenSessionAsync();
        _time.Advance(TimeSpan.FromMinutes(1));
        var before = await RowAsync(userId);

        var ticket = await Store.RetrieveAsync(key, OrdinaryRoute(), Ct);

        Assert.NotNull(ticket);
        Assert.NotEqual(before, await RowAsync(userId));
        Assert.Equal(1, await _scenario.ScalarAsync(
            "select count(*) from identity.login_sessions where user_id = $1 and last_seen_at = $2", userId, _time.GetUtcNow()));
    }

    [Fact]
    public async Task OnAReadOnlyRouteTheTicketStoreNeverRevokes()
    {
        var (userId, key) = await OpenSessionAsync();

        await Store.RemoveAsync(key, ReadOnlyRoute(), Ct);
        Assert.Equal(0, await _scenario.ScalarAsync(
            "select count(*) from identity.login_sessions where user_id = $1 and revoked_at is not null", userId));

        await Store.RemoveAsync(key, OrdinaryRoute(), Ct);
        Assert.Equal(1, await _scenario.ScalarAsync(
            "select count(*) from identity.login_sessions where user_id = $1 and revoked_at is not null", userId));
    }

    [Fact]
    public async Task OnAReadOnlyRouteTheTicketStoreRefusesToStoreOrRenew()
    {
        var (userId, key) = await OpenSessionAsync();
        var ticket = await Store.RetrieveAsync(key, ReadOnlyRoute(), Ct);
        Assert.NotNull(ticket);
        var before = await RowAsync(userId);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Store.RenewAsync(key, ticket, ReadOnlyRoute(), Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store.StoreAsync(ticket, ReadOnlyRoute(), Ct));

        Assert.Equal(before, await RowAsync(userId));
        Assert.Equal(1, await _scenario.ScalarAsync("select count(*) from identity.login_sessions where user_id = $1", userId));
    }

    private async Task<(Guid UserId, string Key)> OpenSessionAsync()
    {
        var account = await _scenario.CreateConfirmedAccountAsync();
        var stamp = await _scenario.ScalarTextAsync("select security_stamp from identity.users where id = $1", account.UserId);
        var principal = DuoraClaims.ForSignIn(new SessionGrant(account.UserId, stamp, SessionPolicy.Normal, null, null, null));
        var key = await Store.StoreAsync(new AuthenticationTicket(principal, DuoraAuthentication.Scheme), OrdinaryRoute(), Ct);
        return (account.UserId, key);
    }

    private async Task<string> InvalidateAsync(string invalidation, Guid userId, string key)
    {
        switch (invalidation)
        {
            case "revoked":
                await _scenario.ScalarAsync(
                    "with s as (update identity.login_sessions set revoked_at = now(), revocation_reason = 'test' where user_id = $1 returning 1) select count(*) from s",
                    userId);
                break;
            case "idle-expired":
                _time.Advance(SessionLifetime.For(SessionPolicy.Normal).Idle);
                break;
            case "absolute-expired":
                await _scenario.ScalarAsync(
                    "with s as (update identity.login_sessions set absolute_expires_at = $2, idle_expires_at = $2 where user_id = $1 returning 1) select count(*) from s",
                    userId, _time.GetUtcNow());
                break;
            case "security-stamp-changed":
                await _scenario.ScalarAsync(
                    "with u as (update identity.users set security_stamp = gen_random_uuid()::text where id = $1 returning 1) select count(*) from u",
                    userId);
                break;
            case "temporary-password-expired":
                await _scenario.ScalarAsync(
                    "with u as (update identity.users set temporary_password_expires_at = $2 where id = $1 returning 1) select count(*) from u",
                    userId, _time.GetUtcNow());
                break;
            case "unknown-key":
                return key + "x";
            default:
                throw new ArgumentOutOfRangeException(nameof(invalidation), invalidation, "Invalidação sem caso no teste.");
        }

        return key;
    }

    private Task<string> RowAsync(Guid userId) => _scenario.ScalarTextAsync(
        "select jsonb_agg(to_jsonb(s) order by s.id)::text from identity.login_sessions s where s.user_id = $1", userId);

    private static DefaultHttpContext ReadOnlyRoute() => RouteWith(new ReadOnlySessionEndpoint());

    private static DefaultHttpContext OrdinaryRoute() => RouteWith();

    private static DefaultHttpContext RouteWith(params object[] metadata)
    {
        var context = new DefaultHttpContext();
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(metadata), "test"));
        return context;
    }
}
