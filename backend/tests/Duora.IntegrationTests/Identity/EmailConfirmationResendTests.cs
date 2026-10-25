using System.Data.Common;
using System.Diagnostics;
using System.Net;
using Duora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Duora.IntegrationTests.Identity;

public sealed class EmailConfirmationResendTests(PostgresFixture postgres)
{
    private const string Requests = "/api/v1/auth/email-confirmation-requests";

    [Fact]
    public async Task ResendPreservesPasswordConsentsAndPendingDeadlineAndInvalidatesTheLegacyProof()
    {
        await using var factory = new DuoraWebFactory(postgres.ConnectionString);
        var scenario = new IdentityScenario(postgres, factory);
        var email = IdentityScenario.NewEmail();
        using var browser = new Browser(factory);
        using var registration = await IdentityScenario.RegisterAsync(browser, email);
        var oldProof = await scenario.CreateLegacyConfirmationProofAsync(email);
        var before = await SnapshotAsync(scenario, oldProof.UserId);

        using var requested = await browser.PostJsonAsync(Requests, new { email, password = IdentityScenario.ValidPassword });
        var replacement = await scenario.ReadLatestLinkAsync(email, "EmailConfirmation");
        var after = await SnapshotAsync(scenario, oldProof.UserId);
        using var stale = await ConfirmAsync(browser, oldProof);
        using var confirmed = await ConfirmAsync(browser, replacement);

        Assert.Equal(HttpStatusCode.Accepted, requested.StatusCode);
        Assert.Equal(before, after);
        Assert.Equal(oldProof.UserId, replacement.UserId);
        await ProblemAssert.IsProblemAsync(stale, HttpStatusCode.BadRequest, "INVALID_TOKEN");
        Assert.Equal(HttpStatusCode.NoContent, confirmed.StatusCode);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("confirmed")]
    [InlineData("wrong-password")]
    [InlineData("expired")]
    public async Task UnavailableRequestsAreNeutralAndCannotIssueAProofOrCreateAnAccount(string kind)
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = new DuoraWebFactory(postgres.ConnectionString) { Time = time };
        var scenario = new IdentityScenario(postgres, factory);
        var email = IdentityScenario.NewEmail();
        using var browser = new Browser(factory);
        if (kind == "confirmed")
        {
            email = (await scenario.CreateConfirmedAccountAsync()).Email;
        }
        else if (kind != "unknown")
        {
            using var registration = await IdentityScenario.RegisterAsync(browser, email);
            if (kind == "expired")
            {
                time.Advance(TimeSpan.FromHours(72));
            }
        }
        await browser.FetchCsrfTokenAsync();
        var accounts = await scenario.CountUsersAsync(email);
        var emails = await scenario.CountOutboxEventsAsync(email);
        var started = Stopwatch.StartNew();

        using var response = await browser.PostJsonAsync(Requests, new
        {
            email,
            password = kind == "wrong-password" ? "uma senha que não confere" : IdentityScenario.ValidPassword,
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Null(response.Headers.Location);
        Assert.Equal(accounts, await scenario.CountUsersAsync(email));
        Assert.Equal(emails, await scenario.CountOutboxEventsAsync(email));
        Assert.True(started.Elapsed >= TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public async Task RegistrationAndResendShareThePersistentIssuanceBudgetAcrossRestarts()
    {
        var email = IdentityScenario.NewEmail();
        await using (var factory = new DuoraWebFactory(postgres.ConnectionString))
        {
            using var browser = new Browser(factory);
            using var registration = await IdentityScenario.RegisterAsync(browser, email);
        }
        for (var i = 0; i < 3; i++)
        {
            await using var factory = new DuoraWebFactory(postgres.ConnectionString);
            using var browser = new Browser(factory);
            await browser.FetchCsrfTokenAsync();
            using var response = await browser.PostJsonAsync(Requests, new { email, password = IdentityScenario.ValidPassword });
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }
        await using var read = new DuoraWebFactory(postgres.ConnectionString);
        var scenario = new IdentityScenario(postgres, read);
        Assert.Equal(3, await scenario.CountOutboxEventsAsync(email));
        using var owner = new Browser(read);
        await owner.FetchCsrfTokenAsync();
        using var latest = await ConfirmAsync(owner, await scenario.ReadLatestLinkAsync(email, "EmailConfirmation"));
        Assert.Equal(HttpStatusCode.NoContent, latest.StatusCode);
    }

    [Fact]
    public async Task ConfirmationCommittingAfterTheLookupPreventsResendFromItsStaleSnapshot()
    {
        var hook = new LookupHook();
        await using var factory = WithInterceptor(hook);
        var scenario = new IdentityScenario(postgres, factory);
        var email = IdentityScenario.NewEmail();
        using var requester = new Browser(factory);
        using var owner = new Browser(factory);
        using var registration = await IdentityScenario.RegisterAsync(requester, email);
        var proof = await scenario.ReadLatestLinkAsync(email, "EmailConfirmation");
        await owner.FetchCsrfTokenAsync();
        hook.Arm(command => MatchesEmail(command, email), async () =>
        {
            using var confirmed = await ConfirmAsync(owner, proof);
            Assert.Equal(HttpStatusCode.NoContent, confirmed.StatusCode);
        });

        using var response = await requester.PostJsonAsync(Requests, new { email, password = IdentityScenario.ValidPassword });

        Assert.True(hook.Fired);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(1, await scenario.CountOutboxEventsAsync(email));
    }

    [Fact]
    public async Task ResendCommittingAfterTheConfirmationLookupInvalidatesItsStaleLegacyProof()
    {
        var hook = new LookupHook();
        await using var factory = WithInterceptor(hook);
        var scenario = new IdentityScenario(postgres, factory);
        var email = IdentityScenario.NewEmail();
        using var confirmer = new Browser(factory);
        using var requester = new Browser(factory);
        using var registration = await IdentityScenario.RegisterAsync(confirmer, email);
        var oldProof = await scenario.CreateLegacyConfirmationProofAsync(email);
        await requester.FetchCsrfTokenAsync();
        hook.Arm(command => MatchesId(command, oldProof.UserId), async () =>
        {
            using var requested = await requester.PostJsonAsync(Requests, new { email, password = IdentityScenario.ValidPassword });
            Assert.Equal(HttpStatusCode.Accepted, requested.StatusCode);
        });

        using var stale = await ConfirmAsync(confirmer, oldProof);

        Assert.True(hook.Fired);
        await ProblemAssert.IsProblemAsync(stale, HttpStatusCode.BadRequest, "INVALID_TOKEN");
        Assert.Equal(2, await scenario.CountOutboxEventsAsync(email));
        using var latest = await ConfirmAsync(confirmer, await scenario.ReadLatestLinkAsync(email, "EmailConfirmation"));
        Assert.Equal(HttpStatusCode.NoContent, latest.StatusCode);
    }

    [Fact]
    public async Task TwoResendsLookingUpBeforeEitherLockCannotExceedTheLastPersistentPermit()
    {
        var email = IdentityScenario.NewEmail();
        var barrier = new TwoLookups(email);
        await using var factory = WithInterceptor(barrier, unlimitedMemoryCounters: true);
        var scenario = new IdentityScenario(postgres, factory);
        using var first = new Browser(factory);
        using var second = new Browser(factory);
        using var registration = await IdentityScenario.RegisterAsync(first, email);
        using var previous = await first.PostJsonAsync(Requests, new { email, password = IdentityScenario.ValidPassword });
        await second.FetchCsrfTokenAsync();
        Assert.Equal(2, await scenario.CountOutboxEventsAsync(email));
        barrier.Arm();

        var requests = await Task.WhenAll(
            first.PostJsonAsync(Requests, new { email, password = IdentityScenario.ValidPassword }),
            second.PostJsonAsync(Requests, new { email, password = IdentityScenario.ValidPassword }));
        try
        {
            Assert.True(barrier.BothArrived);
            Assert.All(requests, response => Assert.Equal(HttpStatusCode.Accepted, response.StatusCode));
            Assert.Equal(3, await scenario.CountOutboxEventsAsync(email));
        }
        finally
        {
            foreach (var response in requests)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task ARegistrationReplacingThePasswordAfterLookupPreventsResendWithTheOldPassword()
    {
        const string NewPassword = "nova senha escolhida pela titular";
        var hook = new LookupHook();
        await using var factory = WithInterceptor(hook);
        var scenario = new IdentityScenario(postgres, factory);
        var email = IdentityScenario.NewEmail();
        using var requester = new Browser(factory);
        using var registrar = new Browser(factory);
        using var registration = await IdentityScenario.RegisterAsync(requester, email);
        hook.Arm(command => MatchesEmail(command, email), async () =>
        {
            using var replacement = await IdentityScenario.RegisterAsync(registrar, email, NewPassword);
            Assert.Equal(HttpStatusCode.Accepted, replacement.StatusCode);
        });

        using var response = await requester.PostJsonAsync(Requests, new { email, password = IdentityScenario.ValidPassword });

        Assert.True(hook.Fired);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(2, await scenario.CountOutboxEventsAsync(email));
        var proof = await scenario.ReadLatestLinkAsync(email, "EmailConfirmation");
        using var confirmed = await registrar.PostJsonAsync("/api/v1/auth/confirm-email", new
        {
            userId = proof.UserId, token = proof.Token, password = NewPassword,
        });
        Assert.Equal(HttpStatusCode.NoContent, confirmed.StatusCode);
    }

    [Fact]
    public async Task OutboxFailureRollsBackTheStampProofAndPersistentBudgetAndAllowsRetry()
    {
        var failure = new RejectNextSave();
        await using var factory = WithInterceptor(failure);
        var scenario = new IdentityScenario(postgres, factory);
        var email = IdentityScenario.NewEmail();
        using var browser = new Browser(factory);
        using var registration = await IdentityScenario.RegisterAsync(browser, email);
        var proof = await scenario.ReadLatestLinkAsync(email, "EmailConfirmation");
        var before = await ProofStateAsync(scenario, proof.UserId);
        failure.Arm();

        using var response = await browser.PostJsonAsync(Requests, new { email, password = IdentityScenario.ValidPassword });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.True(failure.Fired);
        Assert.Equal(before, await ProofStateAsync(scenario, proof.UserId));
        Assert.Equal(1, await scenario.CountOutboxEventsAsync(email));
        using var retry = await browser.PostJsonAsync(Requests, new { email, password = IdentityScenario.ValidPassword });
        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        Assert.Equal(2, await scenario.CountOutboxEventsAsync(email));
        using var confirmed = await ConfirmAsync(browser, await scenario.ReadLatestLinkAsync(email, "EmailConfirmation"));
        Assert.Equal(HttpStatusCode.NoContent, confirmed.StatusCode);
    }

    [Fact]
    public async Task ResendUsesTheRegistrationAccountLimitAndReturnsRetryAfter()
    {
        await using var factory = new DuoraWebFactory(postgres.ConnectionString)
        {
            Settings = new Dictionary<string, string?> { ["RateLimiting:RegisterPerAccount:PermitLimit"] = "1" },
        };
        using var browser = new Browser(factory);
        var email = IdentityScenario.NewEmail();
        using var registration = await IdentityScenario.RegisterAsync(browser, email);
        using var response = await browser.PostJsonAsync(Requests, new { email, password = IdentityScenario.ValidPassword });
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.NotNull(response.Headers.RetryAfter);
    }

    private DuoraWebFactory WithInterceptor(IInterceptor interceptor, bool unlimitedMemoryCounters = false) =>
        new(postgres.ConnectionString)
        {
            TestServices = services => services.ConfigureDbContext<DuoraDbContext>(options => options.AddInterceptors(interceptor)),
            Settings = unlimitedMemoryCounters ? new Dictionary<string, string?>
            {
                ["RateLimiting:RegisterPerAccount:PermitLimit"] = "1000",
                ["RateLimiting:RegisterPerIp:PermitLimit"] = "1000",
            } : new Dictionary<string, string?>(),
        };

    private static bool MatchesEmail(DbCommand command, string email) =>
        command.Parameters.Cast<DbParameter>().Any(parameter =>
            string.Equals(parameter.Value as string, email.ToUpperInvariant(), StringComparison.Ordinal));

    private static bool MatchesId(DbCommand command, Guid id) =>
        command.Parameters.Cast<DbParameter>().Any(parameter =>
            parameter.Value is Guid value && value == id
            || string.Equals(parameter.Value as string, id.ToString("D"), StringComparison.OrdinalIgnoreCase));

    // Run the other real HTTP operation after the SELECT has captured its row but before EF materializes it.
    // No account gate/row lock is held yet, so the callback can commit without reentrant lock deadlock.
    private sealed class LookupHook : DbCommandInterceptor
    {
        private Func<DbCommand, bool>? _matches;
        private Func<Task>? _action;
        private int _armed;
        public bool Fired { get; private set; }

        public void Arm(Func<DbCommand, bool> matches, Func<Task> action)
        {
            _matches = matches;
            _action = action;
            Volatile.Write(ref _armed, 1);
        }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _armed) == 1 && _matches!(command) && Interlocked.Exchange(ref _armed, 0) == 1)
            {
                Fired = true;
                await _action!();
            }
            return result;
        }
    }

    private sealed class TwoLookups(string email) : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _both = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _armed;
        private int _arrived;
        public bool BothArrived => Volatile.Read(ref _arrived) == 2;
        public void Arm() => Volatile.Write(ref _armed, 1);

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _armed) == 1 && MatchesEmail(command, email))
            {
                if (Interlocked.Increment(ref _arrived) == 2)
                {
                    _both.TrySetResult();
                }
                await _both.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }
            return result;
        }
    }

    private sealed class RejectNextSave : SaveChangesInterceptor
    {
        private bool _armed;
        public bool Fired { get; private set; }
        public void Arm() => _armed = true;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (_armed)
            {
                _armed = false;
                Fired = true;
                throw new InvalidOperationException("Injected synthetic outbox failure.");
            }
            return ValueTask.FromResult(result);
        }
    }

    private static Task<string> ProofStateAsync(IdentityScenario scenario, Guid id) =>
        scenario.ScalarTextAsync("""
            select md5(concat(u.security_stamp,'|',u.concurrency_stamp,'|',
              (select jsonb_agg(to_jsonb(t) order by t.login_provider,t.name)::text
               from identity.user_tokens t where t.user_id=u.id)))
            from identity.users u where u.id=$1
            """, id);

    private static Task<HttpResponseMessage> ConfirmAsync(Browser browser, AccountLink proof) =>
        browser.PostJsonAsync("/api/v1/auth/confirm-email", new
        {
            userId = proof.UserId, token = proof.Token, password = IdentityScenario.ValidPassword,
        });

    private static async Task<string> SnapshotAsync(IdentityScenario scenario, Guid id) =>
        await scenario.ScalarTextAsync("""
            select md5(concat(u.password_hash,'|',u.created_at::text,'|',
              (select jsonb_agg(to_jsonb(c) order by c.id)::text
               from identity.consent_records c where c.user_id=u.id)))
            from identity.users u where u.id=$1
            """, id);
}
