using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Duora.Core.Events;
using Duora.Core.Identity;
using Duora.Core.Safety;
using Duora.Infrastructure.Safety;
using Duora.IntegrationTests.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Duora.IntegrationTests.Safety;

/// <summary>
/// API-ADM25 (issue #321) num banco próprio, com descritor de corte sintético: a prévia lista a unidade legada com
/// a allowlist de campos e não produz mutação lógica alguma — nem na sessão (sem toque, renovação ou revogação),
/// nem em auditoria, outbox, guardas, jobs, sequências ou cookies. As demais rotas continuam deslizando a sessão.
/// </summary>
[Trait("Acceptance", "T-10")]
public sealed class RegistrationReconciliationPreviewEffectsTests(PostgresFixture postgres)
{
    private const string EvidenceId = "synthetic-321-r1";
    private const string Path = RegistrationReconciliationPreviewHttpTests.Path;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheModeratorPreviewsTheLegacyUnitWithOnlyTheAllowlistedFields()
    {
        await using var s = await ScenarioAsync();
        var measure = await SuspendBeforePolicyAsync(s, s.Seed.Alice);
        await using var host = Host(s);
        using var moderator = await ModeratorAsync(s, host);

        using var response = await moderator.GetAsync(Path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var text = await response.Content.ReadAsStringAsync(Ct);
        var body = JsonNode.Parse(text)!.AsObject();
        Assert.Equal(EvidenceId, (string?)body["evidenceId"]);
        Assert.Equal(1, (int)body["selectorVersion"]!);
        Assert.Equal((1, 0), ((int)body["actionableRegistrations"]!, (int)body["heldRegistrations"]!));
        Assert.Empty(body["registrationsByPrimaryIssue"]!.AsArray());
        var units = body["units"]!.AsObject();
        Assert.Equal((1, 0, 0), ((int)units["eligible"]!, (int)units["blocked"]!, (int)units["oversized"]!));
        Assert.Null(body["next"]);
        var unit = Assert.Single(body["page"]!.AsArray())!.AsObject();
        Assert.Equal(["items", "measureVersion", "originalMeasureId", "subjectUserId", "unitDigest"],
            unit.Select(p => p.Key).Order(StringComparer.Ordinal));
        Assert.Equal((s.Seed.Alice, measure, 1L),
            (Guid.Parse((string)unit["subjectUserId"]!), Guid.Parse((string)unit["originalMeasureId"]!), (long)unit["measureVersion"]!));
        var item = Assert.Single(unit["items"]!.AsArray())!.AsObject();
        Assert.Equal(["eventId", "maxPromotions", "maxReleasedSeats", "registrationVersion", "state"],
            item.Select(p => p.Key).Order(StringComparer.Ordinal));
        Assert.Equal((s.EventId, 1L, nameof(RegistrationState.Confirmed), 1, 1),
            (Guid.Parse((string)item["eventId"]!), (long)item["registrationVersion"]!, (string?)item["state"],
                (int)item["maxReleasedSeats"]!, (int)item["maxPromotions"]!));
        Assert.True(ReconciliationUnitDigest.IsWellFormed((string?)unit["unitDigest"]));
        foreach (var user in new[] { s.Seed.Alice, s.Seed.Bob, s.Seed.Third })
        {
            var email = await s.TextAsync("select email from identity.users where id=$1", user);
            Assert.DoesNotContain(email, text, StringComparison.OrdinalIgnoreCase);
        }
        Assert.DoesNotContain("Synthetic legacy fixture", text, StringComparison.Ordinal);
        Assert.DoesNotContain(s.Seed.Bob.ToString(), text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheCursorResumesAfterTheLastUnitOfThePreviousPage()
    {
        await using var s = await ScenarioAsync();
        var measure = await SuspendBeforePolicyAsync(s, s.Seed.Alice);
        await using var host = Host(s);
        using var moderator = await ModeratorAsync(s, host);

        using var response = await moderator.GetAsync($"{Path}?cursorSubject={s.Seed.Alice}&cursorMeasure={measure}");

        var body = await MfaScenario.ReadObjectAsync(response);
        Assert.Empty(body["page"]!.AsArray());
        Assert.Equal(1, (int)body["actionableRegistrations"]!);
        Assert.Equal(1, (int)body["units"]!["eligible"]!);
    }

    [Fact]
    public async Task ThePreviewWritesNothingAndDoesNotRenewTheSession()
    {
        await using var s = await ScenarioAsync();
        await SuspendBeforePolicyAsync(s, s.Seed.Alice);
        await using var host = Host(s);
        using var moderator = await ModeratorAsync(s, host);
        s.Clock.Advance(TimeSpan.FromMinutes(1));
        var before = await FingerprintAsync(s);
        Assert.Contains("identity.login_sessions=", before, StringComparison.Ordinal);
        Assert.Contains("ops.audit_events=", before, StringComparison.Ordinal);
        Assert.Contains("ops.outbox_events=", before, StringComparison.Ordinal);
        Assert.Contains("safety.user_guards=", before, StringComparison.Ordinal);
        var cookies = CookiesOf(moderator);

        using var response = await moderator.GetAsync(Path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Equal(cookies, CookiesOf(moderator));
        Assert.Equal(before, await FingerprintAsync(s));
    }

    [Fact]
    public async Task RefusedPreviewsWriteNothingEither()
    {
        await using var s = await ScenarioAsync();
        await SuspendBeforePolicyAsync(s, s.Seed.Alice);
        await using var host = Host(s);
        var scenario = new AccountSuspensionScenario(s.Database.AppConnectionString, host, s.Clock);
        var (_, @operator) = await scenario.PrivilegedBrowserAsync(AccountRoles.Operator);
        using var _ = @operator;
        using var moderator = await ModeratorAsync(s, host);
        s.Clock.Advance(TimeSpan.FromMinutes(1));
        var before = await FingerprintAsync(s);

        using var forbidden = await @operator.GetAsync(Path);
        using var invalid = await moderator.GetAsync($"{Path}?cursorSubject=x");
        using var stranger = new Browser(host);
        using var anonymous = await stranger.GetWithoutContextAsync(Path);

        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(before, await FingerprintAsync(s));
    }

    /// <summary>Limite em memória por conta moderadora: 6 prévias por minuto (ADR-0048).</summary>
    [Fact]
    public async Task BeyondTheLimitPerModeratorThePreviewIsTooManyRequestsAndWritesNothing()
    {
        await using var s = await ScenarioAsync();
        await SuspendBeforePolicyAsync(s, s.Seed.Alice);
        await using var host = Host(s);
        using var moderator = await ModeratorAsync(s, host);
        using var other = await ModeratorAsync(s, host);
        for (var attempt = 1; attempt <= 6; attempt++)
        {
            using var allowed = await moderator.GetAsync(attempt == 6 ? $"{Path}?cursorSubject=x" : Path);
            Assert.True(allowed.StatusCode is HttpStatusCode.OK or HttpStatusCode.BadRequest, $"Prévia {attempt}: {allowed.StatusCode}.");
        }
        var before = await FingerprintAsync(s);
        var cookies = CookiesOf(moderator);

        using var limited = await moderator.GetAsync(Path);
        using var otherModerator = await other.GetAsync(Path);

        var body = await ProblemAssert.IsProblemAsync(limited, HttpStatusCode.TooManyRequests, "RATE_LIMITED");
        Assert.True(ProblemAssert.RetryAfterSeconds(limited) is > 0 and <= 60);
        Assert.Equal(["code", "status", "title", "traceId", "type"], body.Select(p => p.Key).Order(StringComparer.Ordinal));
        Assert.False(limited.Headers.Contains("Set-Cookie"));
        Assert.Equal(HttpStatusCode.OK, otherModerator.StatusCode);
        Assert.Equal(cookies, CookiesOf(moderator));
        Assert.Equal(before, await FingerprintAsync(s));
    }

    /// <summary>A chave do limite é a conta: outra sessão da mesma pessoa moderadora não recebe cota nova.</summary>
    [Fact]
    public async Task AnotherSessionOfTheSameModeratorSharesTheLimit()
    {
        await using var s = await ScenarioAsync();
        await using var host = Host(s);
        var scenario = new AccountSuspensionScenario(s.Database.AppConnectionString, host, s.Clock);
        var account = await scenario.Identity.CreatePrivilegedAccountAsync(AccountRoles.Moderator);
        await scenario.ExecuteAsync("update identity.users set status = 'Active' where id = $1", account.UserId);
        using var first = await scenario.Identity.SignedInBrowserAsync(account);
        var mfa = await MfaScenario.EnableAsync(first, s.Clock);
        using var second = new Browser(host);
        using (var login = await MfaScenario.LoginAsync(second, account, twoFactorCode: Identity.Totp.Next(s.Clock, mfa.SharedKey)))
        {
            Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        }
        await second.FetchPrivateContextAsync();
        Assert.NotEqual(await MfaScenario.ReadSessionIdAsync(first), await MfaScenario.ReadSessionIdAsync(second));
        for (var attempt = 1; attempt <= 6; attempt++)
        {
            using var allowed = await first.GetAsync(Path);
            Assert.True(allowed.StatusCode == HttpStatusCode.OK, $"Prévia {attempt}: {allowed.StatusCode}.");
        }

        using var limited = await second.GetAsync(Path);

        await ProblemAssert.IsProblemAsync(limited, HttpStatusCode.TooManyRequests, "RATE_LIMITED");
    }

    /// <summary>
    /// O limite responde sem ler o banco de negócio: com a cota esgotada e <c>events.registrations</c> travada, o pedido
    /// seguinte é 429, não o 503 do timeout. Sessão e policies leem só tabelas de <c>identity</c>.
    /// </summary>
    [Fact]
    public async Task TheLimitAnswersWithoutReadingTheSnapshot()
    {
        await using var s = await ScenarioAsync();
        await using var host = Host(s, statementTimeout: TimeSpan.FromMilliseconds(200));
        using var moderator = await ModeratorAsync(s, host);
        for (var attempt = 1; attempt <= 6; attempt++)
        {
            using var allowed = await moderator.GetAsync(Path);
            Assert.True(allowed.StatusCode == HttpStatusCode.OK, $"Prévia {attempt}: {allowed.StatusCode}.");
        }

        HttpResponseMessage response;
        await using (var holder = new NpgsqlConnection(s.Database.MigratorConnectionString))
        {
            await holder.OpenAsync(Ct);
            await using var transaction = await holder.BeginTransactionAsync(Ct);
            await using (var exclusive = new NpgsqlCommand("lock table events.registrations in access exclusive mode", holder, transaction))
            {
                await exclusive.ExecuteNonQueryAsync(Ct);
            }
            response = await moderator.GetAsync(Path);
            await transaction.RollbackAsync(Ct);
        }

        using (response)
        {
            await ProblemAssert.IsProblemAsync(response, HttpStatusCode.TooManyRequests, "RATE_LIMITED");
        }
    }

    [Fact]
    public async Task AStatementTimeoutIsTemporarilyUnavailableWithoutDataAndWritesNothing()
    {
        await using var s = await ScenarioAsync();
        await SuspendBeforePolicyAsync(s, s.Seed.Alice);
        await using var host = Host(s, statementTimeout: TimeSpan.FromMilliseconds(200));
        using var moderator = await ModeratorAsync(s, host);
        var before = await FingerprintAsync(s);

        HttpResponseMessage response;
        await using (var holder = new NpgsqlConnection(s.Database.MigratorConnectionString))
        {
            await holder.OpenAsync(Ct);
            await using var transaction = await holder.BeginTransactionAsync(Ct);
            await using (var exclusive = new NpgsqlCommand("lock table events.registrations in access exclusive mode", holder, transaction))
            {
                await exclusive.ExecuteNonQueryAsync(Ct);
            }
            response = await moderator.GetAsync(Path);
            await transaction.RollbackAsync(Ct);
        }

        using (response)
        {
            var body = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.ServiceUnavailable, "TEMPORARILY_UNAVAILABLE");
            Assert.True(ProblemAssert.RetryAfterSeconds(response) > 0);
            Assert.Equal(["code", "status", "title", "traceId", "type"], body.Select(p => p.Key).Order(StringComparer.Ordinal));
        }
        Assert.Equal(before, await FingerprintAsync(s));
    }

    [Fact]
    public async Task ThePreviewNeverExtendsTheIdleLimitAndNeverRevokesTheExpiredSession()
    {
        await using var s = await ScenarioAsync();
        await using var host = Host(s);
        using var moderator = await ModeratorAsync(s, host);
        var idle = SessionLifetime.For(SessionPolicy.Administrative).Idle;

        s.Clock.Advance(idle - TimeSpan.FromMinutes(1));
        using (var stillValid = await moderator.GetAsync(Path))
        {
            Assert.Equal(HttpStatusCode.OK, stillValid.StatusCode);
        }
        s.Clock.Advance(TimeSpan.FromMinutes(1));
        var before = await FingerprintAsync(s);
        using var expired = await moderator.GetAsync(Path);

        await ProblemAssert.IsProblemAsync(expired, HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
        Assert.Equal(before, await FingerprintAsync(s));
    }

    [Theory]
    [InlineData("/api/v1/me")]
    [InlineData("/api/v1/admin/accounts/{subject}/suspension")]
    public async Task EveryOtherRouteStillSlidesTheSession(string route)
    {
        await using var s = await ScenarioAsync();
        await using var host = Host(s);
        using var moderator = await ModeratorAsync(s, host);
        s.Clock.Advance(TimeSpan.FromMinutes(1));
        var now = s.Clock.GetUtcNow();
        var idle = SessionLifetime.For(SessionPolicy.Administrative).Idle;

        using var response = await moderator.GetAsync(route.Replace("{subject}", s.Seed.Alice.ToString(), StringComparison.Ordinal));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, await s.CountAsync(
            "select count(*) from identity.login_sessions where revoked_at is null and last_seen_at=$1 and idle_expires_at=$2",
            now, now + idle));
    }

    [Fact]
    public async Task TheReaderRechecksTheModeratorSessionInsideItsOwnSnapshot()
    {
        await using var s = await ScenarioAsync();
        await SuspendBeforePolicyAsync(s, s.Seed.Alice);
        await using var host = Host(s);
        var scenario = new AccountSuspensionScenario(s.Database.AppConnectionString, host, s.Clock);
        var (account, browser) = await scenario.PrivilegedBrowserAsync();
        using var _ = browser;
        var session = new ModeratorSession(account.UserId, await MfaScenario.ReadSessionIdAsync(browser));
        var previews = host.Services.GetRequiredService<LegacyRegistrationReconciliationPreviews>();

        Assert.IsType<ReconciliationPreviewOutcome.Ready>(await previews.ReadAsModeratorAsync(session, evidence: null, after: null, Ct));
        Assert.IsType<ReconciliationPreviewOutcome.Forbidden>(await previews.ReadAsModeratorAsync(session with { SessionId = Guid.NewGuid() }, null, null, Ct));
        Assert.IsType<ReconciliationPreviewOutcome.Forbidden>(await previews.ReadAsModeratorAsync(session with { UserId = s.Seed.Alice }, null, null, Ct));

        await s.ExecuteAsync("update identity.login_sessions set totp_verified_at=$2 where id=$1",
            session.SessionId, s.Clock.GetUtcNow() - RecentAuthentication.Window - TimeSpan.FromTicks(10));
        Assert.IsType<ReconciliationPreviewOutcome.Forbidden>(await previews.ReadAsModeratorAsync(session, null, null, Ct));
        await s.ExecuteAsync("update identity.login_sessions set totp_verified_at=$2 where id=$1",
            session.SessionId, s.Clock.GetUtcNow().AddMinutes(1));
        Assert.IsType<ReconciliationPreviewOutcome.Forbidden>(await previews.ReadAsModeratorAsync(session, null, null, Ct));
        await s.ExecuteAsync("update identity.login_sessions set totp_verified_at=$2 where id=$1",
            session.SessionId, s.Clock.GetUtcNow());
        Assert.IsType<ReconciliationPreviewOutcome.Ready>(await previews.ReadAsModeratorAsync(session, null, null, Ct));

        await s.ExecuteAsync(
            "delete from identity.user_roles where user_id=$1 and role_id=(select id from identity.roles where name=$2)",
            account.UserId, AccountRoles.Moderator);
        Assert.IsType<ReconciliationPreviewOutcome.Forbidden>(await previews.ReadAsModeratorAsync(session, null, null, Ct));
    }

    private async Task<SuspendedRegistrationScenario> ScenarioAsync()
    {
        var s = await SuspendedRegistrationScenario.CreateAsync(postgres);
        await s.ExecuteAsync("update events.events set starts_at=$2 where id=$1", s.EventId, s.Clock.GetUtcNow().AddHours(1));
        await s.RegisterAsync(s.Seed.Alice);
        await s.RegisterAsync(s.Seed.Bob);
        await s.RegisterAsync(s.Seed.Third);
        return s;
    }

    /// <summary>Suspensão anterior à política, inserida direto: o escritor atual retiraria a inscrição na hora.</summary>
    internal static async Task<Guid> SuspendBeforePolicyAsync(SuspendedRegistrationScenario s, Guid subject)
    {
        var measure = Guid.NewGuid();
        await s.ExecuteAsync("""
            insert into safety.moderation_actions(id,subject_user_id,measure,reason_code,internal_reason,
              author_kind,decided_at,command_key,command_intent,source_code,source_id)
            values($1,$2,'Suspension','SelfieVerificationFailed','Synthetic legacy fixture',
              'Automated',$3,encode(sha256($1::text::bytea),'hex'),sha256($2::text::bytea),'synthetic',$1);
            """, measure, subject, s.Clock.GetUtcNow().AddSeconds(10));
        await s.ExecuteAsync("update identity.users set status_before_suspension=status,status='Suspended' where id=$1", subject);
        return measure;
    }

    /// <summary>Web sobre o banco do cenário, com um descritor de corte sintético posterior às fixtures.</summary>
    internal static DuoraWebFactory Host(SuspendedRegistrationScenario s, TimeSpan? statementTimeout = null) =>
        new(s.Database.AppConnectionString)
        {
            Time = s.Clock,
            // Só o teto do statement_timeout muda, para provocar o timeout sem esperar os 10 s de produção.
            TestServices = statementTimeout is not { } timeout ? null : services =>
            {
                services.RemoveAll<LegacyRegistrationReconciliationPreviews>();
                services.AddSingleton(provider => new LegacyRegistrationReconciliationPreviews(
                    provider.GetRequiredService<NpgsqlDataSource>(), provider.GetRequiredService<TimeProvider>(),
                    LegacyRegistrationReconciliationPreviews.MaxUnitsPerPage,
                    LegacyRegistrationReconciliationPreviews.MaxItemsPerPage, timeout));
            },
            Settings = new Dictionary<string, string?>
            {
                ["RegistrationReconciliation:Cutover:EvidenceId"] = EvidenceId,
                ["RegistrationReconciliation:Cutover:Lower"] = Utc(s.Clock.GetUtcNow().AddMinutes(1)),
                ["RegistrationReconciliation:Cutover:Upper"] = Utc(s.Clock.GetUtcNow().AddMinutes(2)),
            },
        };

    private static string Utc(DateTimeOffset instant) =>
        instant.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);

    internal static async Task<Browser> ModeratorAsync(SuspendedRegistrationScenario s, DuoraWebFactory host)
    {
        var scenario = new AccountSuspensionScenario(s.Database.AppConnectionString, host, s.Clock);
        var (_, browser) = await scenario.PrivilegedBrowserAsync();
        return browser;
    }

    private static string CookiesOf(Browser browser) => string.Join('\n', browser.Cookies.GetAllCookies()
        .Select(cookie => $"{cookie.Name}={cookie.Value};{cookie.Expires:O};{cookie.Path}").Order(StringComparer.Ordinal));

    /// <summary>
    /// Toda tabela dos schemas da aplicação (contagem e digest das linhas) e o valor de toda sequência, lidos com o
    /// papel do migrador, que enxerga também as tabelas fechadas ao papel da aplicação.
    /// </summary>
    internal static async Task<string> FingerprintAsync(SuspendedRegistrationScenario s,
        string schemas = "'identity','profiles','events','experiences','social','safety','media','ops','privacy'",
        bool includeSequences = true)
    {
        await using var connection = new NpgsqlConnection(s.Database.MigratorConnectionString);
        await connection.OpenAsync(Ct);
        List<string> tables = [];
        await using (var list = new NpgsqlCommand($"""
            select format('%I.%I', table_schema, table_name) from information_schema.tables
            where table_type='BASE TABLE' and table_schema in ({schemas})
            order by 1
            """, connection))
        await using (var reader = await list.ExecuteReaderAsync(Ct))
        {
            while (await reader.ReadAsync(Ct))
            {
                tables.Add(reader.GetString(0));
            }
        }

        var fingerprint = new StringBuilder();
        foreach (var table in tables)
        {
            await using var digest = new NpgsqlCommand(
                $"select count(*)||':'||coalesce(md5(string_agg(t::text, ',' order by t::text)),'') from {table} t", connection);
            fingerprint.Append(table).Append('=').Append(await digest.ExecuteScalarAsync(Ct)).Append('\n');
        }
        if (!includeSequences)
        {
            return fingerprint.ToString();
        }

        await using var sequences = new NpgsqlCommand(
            "select coalesce(string_agg(format('%I.%I=%s', schemaname, sequencename, last_value), ',' order by schemaname, sequencename),'') from pg_sequences",
            connection);
        fingerprint.Append("sequences=").Append(await sequences.ExecuteScalarAsync(Ct));
        return fingerprint.ToString();
    }
}
