using System.Diagnostics;
using System.Net;
using Duora.Core.Identity;
using Duora.Infrastructure.Identity;
using Duora.Web.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Duora.IntegrationTests.Identity;

/// <summary>Issue #65: carga controlada no hash real, sem depender da CPU disponível no runner.</summary>
[Trait("Threat", "TM-06")]
public sealed class LoginConcurrencyTests(PostgresFixture postgres)
{
    /// <summary>Piso de falha do login sem o sorteio (LoginTimingOptions.FailedLoginMinimumDuration).</summary>
    private static readonly TimeSpan FailureFloor = TimeSpan.FromMilliseconds(350);

    /// <summary>Hash lento simulado: CPU carregada, sem saturar o runner.</summary>
    private static readonly TimeSpan SlowHash = TimeSpan.FromMilliseconds(1800);

    /// <summary>Teste do gate sem banco: falha em vez de travar se uma espera perder o teto.</summary>
    private const int GateTestTimeoutMs = 10_000;

    /// <summary>Bem acima do teto da fila do teste (1 s) e bem abaixo dos 30 s padrão.</summary>
    private static readonly TimeSpan QueueCeilingLimit = TimeSpan.FromSeconds(10);

    /// <summary>Hash que termina depois de um prazo de login de 1 s.</summary>
    private static readonly TimeSpan HashPastTheDeadline = TimeSpan.FromMilliseconds(1500);

    /// <summary>Quanto a vez fica presa: bem mais que o piso, para a espera poder consumi-lo se contasse.</summary>
    private static readonly TimeSpan HeldTurn = TimeSpan.FromSeconds(1);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheFailureFloorStartsWhenTheTurnIsReleasedNotWhenTheLoginArrives(bool exists)
    {
        // Hash rápido: com o hash lento dos outros testes o próprio hash cobria o piso e escondia de onde ele conta.
        var normalizer = new SignallingNormalizer();
        await using var factory = new DuoraWebFactory(postgres.ConnectionString)
        {
            TestServices = services =>
            {
                services.AddSingleton<IPasswordHasher<DuoraUser>>(new PasswordHasher<DuoraUser>(
                    Options.Create(new PasswordHasherOptions { IterationCount = 1 })));
                services.AddSingleton<ILookupNormalizer>(normalizer);
            },
        };
        var email = exists
            ? (await new IdentityScenario(postgres, factory).CreateConfirmedAccountAsync()).Email
            : IdentityScenario.NewEmail();
        Assert.True(EmailAddress.TryParse(email, out var address));
        using var browser = new Browser(factory);
        // Aquece o caminho (EF, pool, JIT): sem isso o primeiro login sozinho chega perto do piso e a sabotagem
        // de contar o piso na chegada poderia passar por acaso.
        using (var warmUp = await IdentityScenario.LoginAsync(browser, email, "senha incorreta longa"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, warmUp.StatusCode);
        }

        using var scope = factory.Services.CreateScope();
        var security = scope.ServiceProvider.GetRequiredService<AccountSecurityService>();
        var turn = await security.EnterLoginAsync(address, TestContext.Current.CancellationToken);

        // O login normaliza o e-mail logo antes de entrar na fila: o sinal diz que ele já está esperando a vez.
        var waiting = normalizer.NextEmailNormalizationAsync();
        var login = IdentityScenario.LoginAsync(browser, email, "senha incorreta longa");
        await waiting.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await Task.Delay(HeldTurn, TestContext.Current.CancellationToken);
        var releasedAt = Stopwatch.GetTimestamp();
        await turn.DisposeAsync();
        using var response = await login;
        var afterRelease = Stopwatch.GetElapsedTime(releasedAt);

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");
        Assert.True(afterRelease >= FailureFloor, $"Respondeu {afterRelease} depois de liberar a vez.");
    }

    [Fact]
    public async Task FourSuccessfulLoginsCanQueueLongerThanTheDatabaseLockTimeout()
    {
        var hasher = new SlowHasher();
        await using var factory = CreateFactory(hasher);
        var account = await new IdentityScenario(postgres, factory).CreateConfirmedAccountAsync();
        hasher.Delay = SlowHash;

        var durations = await BurstAsync(factory, account.Email, account.Password, HttpStatusCode.NoContent);

        Assert.Equal(1, hasher.MaximumConcurrent);
        Assert.True(durations.Max() > TimeSpan.FromSeconds(7));
        Assert.Equal(4, hasher.Verifications);
    }

    [Fact]
    public async Task FourInvalidLoginsSerializeForExistingAndMissingEmailsWithComparableLatency()
    {
        var hasher = new SlowHasher();
        await using var factory = CreateFactory(hasher);
        var account = await new IdentityScenario(postgres, factory).CreateConfirmedAccountAsync();
        hasher.Delay = SlowHash;
        var existing = await BurstAsync(factory, account.Email, "senha incorreta longa", HttpStatusCode.Unauthorized);
        var missing = await BurstAsync(factory, IdentityScenario.NewEmail(), "senha incorreta longa", HttpStatusCode.Unauthorized);

        Assert.Equal(1, hasher.MaximumConcurrent);
        Assert.Equal(8, hasher.Verifications);
        // A implementação antiga terminava o endereço inexistente em um hash, não em quatro.
        Assert.True(existing.Max() >= TimeSpan.FromSeconds(7));
        Assert.True(missing.Max() >= TimeSpan.FromSeconds(7));
        // Limites inferiores, não comparação entre os dois: o caminho existente custa mais (transação, lock,
        // auditoria) e a diferença acumula por posição e oscila com a carga. Os quatro saem juntos e cada hash
        // dura pelo menos SlowHash, então o i-ésimo a terminar esperou i + 1 hashes se a fila for serializada.
        for (var i = 0; i < missing.Length; i++)
        {
            Assert.True(missing[i] >= SlowHash * (i + 1), $"Inexistente, posição {i}: {missing[i]}");
            Assert.True(existing[i] >= SlowHash * (i + 1), $"Existente, posição {i}: {existing[i]}");
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task QueueTimeoutHasTheSameHttpContractRegardlessOfAccountExistence(bool exists)
    {
        await using var factory = new DuoraWebFactory(postgres.ConnectionString)
        {
            Settings = new Dictionary<string, string?> { ["Identity:AccountQueueTimeout"] = "00:00:00.150" },
        };
        var email = exists
            ? (await new IdentityScenario(postgres, factory).CreateConfirmedAccountAsync()).Email
            : IdentityScenario.NewEmail();
        Assert.True(EmailAddress.TryParse(email, out var address));
        using var scope = factory.Services.CreateScope();
        var security = scope.ServiceProvider.GetRequiredService<AccountSecurityService>();
        await using (await security.EnterLoginAsync(address, TestContext.Current.CancellationToken))
        {
            using var browser = new Browser(factory);
            // Outra caixa deve disputar a mesma vez, tanto para conta existente quanto inexistente.
            using var response = await IdentityScenario.LoginAsync(browser, email.ToUpperInvariant(), "senha incorreta longa");
            await ProblemAssert.IsProblemAsync(response, HttpStatusCode.ServiceUnavailable, "TEMPORARILY_UNAVAILABLE");
            Assert.Equal(1, ProblemAssert.RetryAfterSeconds(response));
            Assert.Null(browser.Cookie("__Host-duora"));
        }

        using var retryBrowser = new Browser(factory);
        using var retry = await IdentityScenario.LoginAsync(retryBrowser, email, "senha incorreta longa");
        await ProblemAssert.IsProblemAsync(retry, HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ALoginPastItsTotalDeadlineAnswers503BeforeTheQueueTimeout(bool exists)
    {
        // Fila de 30 s (padrão) e prazo total de 1 s: quem decide é o prazo do pedido inteiro, não o teto da fase.
        await using var factory = new DuoraWebFactory(postgres.ConnectionString)
        {
            Settings = new Dictionary<string, string?> { ["Identity:LoginTimeout"] = "00:00:01" },
        };
        var email = exists
            ? (await new IdentityScenario(postgres, factory).CreateConfirmedAccountAsync()).Email
            : IdentityScenario.NewEmail();
        Assert.True(EmailAddress.TryParse(email, out var address));
        using var browser = new Browser(factory);
        await browser.FetchCsrfTokenAsync();
        using var scope = factory.Services.CreateScope();
        var security = scope.ServiceProvider.GetRequiredService<AccountSecurityService>();
        await using (await security.EnterLoginAsync(address, TestContext.Current.CancellationToken))
        {
            var waited = Stopwatch.StartNew();
            using var response = await IdentityScenario.LoginAsync(browser, email, "senha incorreta longa");
            waited.Stop();

            await ProblemAssert.IsProblemAsync(response, HttpStatusCode.ServiceUnavailable, "TEMPORARILY_UNAVAILABLE");
            Assert.Equal(1, ProblemAssert.RetryAfterSeconds(response));
            Assert.Null(browser.Cookie("__Host-duora"));
            Assert.True(waited.Elapsed >= TimeSpan.FromSeconds(1), $"Desistiu cedo: {waited.Elapsed}.");
            Assert.True(waited.Elapsed < TimeSpan.FromSeconds(10), $"Esperou {waited.Elapsed}, além do prazo total.");
        }
    }

    [Fact]
    public async Task AFailureVerifiedAfterTheDeadlineStillCountsTowardsLockout()
    {
        // O prazo vence durante o hash (1,5 s contra 1 s). Gravar e confirmar a falha vem depois e não pode ser
        // cancelado pelo prazo, ou a tentativa sairia de graça do bloqueio (TM-01).
        var hasher = new SlowHasher();
        await using var factory = CreateFactory(hasher, loginTimeout: "00:00:01");
        var account = await new IdentityScenario(postgres, factory).CreateConfirmedAccountAsync();
        hasher.Delay = HashPastTheDeadline;
        using var browser = new Browser(factory);

        using var response = await IdentityScenario.LoginAsync(browser, account.Email, "senha incorreta longa");

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");
        Assert.Equal(1, await AccessFailedCountAsync(factory, account.UserId));
    }

    [Fact]
    public async Task ASuccessVerifiedAfterTheDeadlineStillOpensTheSession()
    {
        var hasher = new SlowHasher();
        await using var factory = CreateFactory(hasher, loginTimeout: "00:00:01");
        var account = await new IdentityScenario(postgres, factory).CreateConfirmedAccountAsync();
        hasher.Delay = HashPastTheDeadline;
        using var browser = new Browser(factory);

        using var response = await IdentityScenario.LoginAsync(browser, account.Email, account.Password);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.NotNull(browser.Cookie("__Host-duora"));
    }

    [Theory]
    [InlineData(450, true)]
    [InlineData(449, false)]
    [InlineData(60000, true)]
    [InlineData(60001, false)]
    public void TheLoginDeadlineMustCoverTheFailureFloorAndStayUnderAMinute(int timeoutMs, bool valid)
    {
        // O padrão do piso é 350 ms mais até 100 ms de sorteio: um prazo menor transformaria toda falha em 503.
        var options = new LoginTimingOptions { LoginTimeout = TimeSpan.FromMilliseconds(timeoutMs) };

        Assert.Equal(valid, options.IsValid());
    }

    [Fact(Timeout = GateTestTimeoutMs)]
    public async Task CancelledAndTimedOutWaitersDoNotLeakOrReleaseAnotherTurn()
    {
        var gate = new AccountCheckGate();
        var key = Guid.NewGuid();
        var timeout = TimeSpan.FromMilliseconds(100);
        var holder = await gate.EnterAsync(key, timeout, TestContext.Current.CancellationToken);
        using var cancelled = new CancellationTokenSource();
        var waiting = gate.EnterAsync(key, TimeSpan.FromSeconds(10), cancelled.Token);
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        await Assert.ThrowsAsync<AccountBusyException>(() => gate.EnterAsync(key, timeout, TestContext.Current.CancellationToken));
        // Uma chave diferente continua independente.
        await using var other = await gate.EnterAsync(Guid.NewGuid(), timeout, TestContext.Current.CancellationToken);
        await holder.DisposeAsync();
        await holder.DisposeAsync();
        await using var next = await gate.EnterAsync(key, timeout, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AReauthenticationWaitingForTheAccountTurnGivesUpAtTheQueueTimeout()
    {
        // Rota sem a vez por e-mail na frente: quem decide é só o teto da fila por UUID (1 s aqui, 30 s padrão).
        await using var factory = new DuoraWebFactory(postgres.ConnectionString)
        {
            Settings = new Dictionary<string, string?> { ["Identity:AccountQueueTimeout"] = "00:00:01" },
        };
        var scenario = new IdentityScenario(postgres, factory);
        var account = await scenario.CreateConfirmedAccountAsync();
        using var browser = await scenario.SignedInBrowserAsync(account);
        var gate = factory.Services.GetRequiredService<AccountCheckGate>();

        await using (await gate.EnterAsync(account.UserId, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken))
        {
            var waited = Stopwatch.StartNew();
            var reauthentication = browser.PostJsonAsync(MfaScenario.Reauthenticate, new { password = account.Password });
            var finished = await Task.WhenAny(reauthentication, Task.Delay(QueueCeilingLimit, TestContext.Current.CancellationToken));
            Assert.True(finished == reauthentication, $"A reautenticação não desistiu em {QueueCeilingLimit}: a fila por UUID está sem teto.");
            using var response = await reauthentication;
            waited.Stop();

            await ProblemAssert.IsProblemAsync(response, HttpStatusCode.ServiceUnavailable, "TEMPORARILY_UNAVAILABLE");
            Assert.Equal(1, ProblemAssert.RetryAfterSeconds(response));
            Assert.True(waited.Elapsed >= TimeSpan.FromSeconds(1), $"Desistiu cedo: {waited.Elapsed}.");
        }
    }

    [Fact(Timeout = GateTestTimeoutMs)]
    public async Task AFailingTransactionDisposeStillReleasesTheAccountTurn()
    {
        var gate = new AccountCheckGate();
        var userId = Guid.NewGuid();
        var timeout = TimeSpan.FromMilliseconds(100);
        var check = new AccountCheck(
            new FailingDisposeTransaction(),
            await gate.EnterAsync(userId, timeout, TestContext.Current.CancellationToken));

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => check.DisposeAsync().AsTask());

        Assert.Equal(FailingDisposeTransaction.Message, failure.Message);
        // Sem o finally a vez ficaria presa e a próxima verificação da conta esperaria até o teto (503).
        await using var next = await gate.EnterAsync(userId, timeout, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TakingTheTurnByEmailWhileHoldingTheAccountTurnFailsLoudly()
    {
        // A ordem é e-mail → UUID → PostgreSQL. Quem pegasse a vez por e-mail com a conta já na vez inverteria a
        // ordem, e duas requisições esperariam uma à outra até o teto (503 em vez de erro de programação).
        await using var factory = new DuoraWebFactory(postgres.ConnectionString);
        var account = await new IdentityScenario(postgres, factory).CreateConfirmedAccountAsync();
        Assert.True(EmailAddress.TryParse(account.Email, out var address));
        using var scope = factory.Services.CreateScope();
        var security = scope.ServiceProvider.GetRequiredService<AccountSecurityService>();
        var user = await scope.ServiceProvider.GetRequiredService<UserManager<DuoraUser>>().FindByIdAsync($"{account.UserId:D}");
        Assert.NotNull(user);
        var ct = TestContext.Current.CancellationToken;

        await using (await security.LockAccountAsync(user, ct))
        {
            var inverted = await Assert.ThrowsAsync<InvalidOperationException>(() => security.EnterLoginAsync(address, ct));
            Assert.Contains("ordem", inverted.Message, StringComparison.Ordinal);
        }

        await using (await factory.Services.GetRequiredService<AccountCheckGate>().EnterAsync(account.UserId, TimeSpan.FromSeconds(1), ct))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => security.EnterLoginAsync(address, ct));
        }

        // Vez da conta devolvida: a ordem certa volta a valer, e e-mail → UUID continua permitido.
        await using var byEmail = await security.EnterLoginAsync(address, ct);
        await using var check = await security.LockAccountAsync(user, ct);
    }

    [Theory]
    [InlineData(99, 5000, false)]
    [InlineData(30000, 99, false)]
    [InlineData(60001, 5000, false)]
    [InlineData(30000, 60001, false)]
    [InlineData(30000, 5000, true)]
    public void QueueAndDatabaseTimeoutsAreValidatedIndependently(int queueMs, int lockMs, bool valid)
    {
        var options = new AccountLockOptions
        {
            AccountQueueTimeout = TimeSpan.FromMilliseconds(queueMs),
            AccountLockTimeout = TimeSpan.FromMilliseconds(lockMs),
        };
        Assert.Equal(valid, options.IsValid());
    }

    private DuoraWebFactory CreateFactory(SlowHasher hasher, string? loginTimeout = null) => new(postgres.ConnectionString)
    {
        TestServices = services => services.AddSingleton<IPasswordHasher<DuoraUser>>(hasher),
        Settings = loginTimeout is null
            ? new Dictionary<string, string?>()
            : new Dictionary<string, string?> { ["Identity:LoginTimeout"] = loginTimeout },
    };

    private static async Task<int> AccessFailedCountAsync(DuoraWebFactory factory, Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var user = await scope.ServiceProvider.GetRequiredService<UserManager<DuoraUser>>().FindByIdAsync($"{userId:D}");
        Assert.NotNull(user);
        return user.AccessFailedCount;
    }

    private static async Task<TimeSpan[]> BurstAsync(
        DuoraWebFactory factory, string email, string password, HttpStatusCode expected)
    {
        var browsers = Enumerable.Range(0, 4).Select(_ => new Browser(factory)).ToArray();
        try
        {
            await Task.WhenAll(browsers.Select(browser => browser.FetchCsrfTokenAsync()));
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var requests = browsers.Select(async browser =>
            {
                await start.Task;
                var clock = Stopwatch.StartNew();
                using var response = await IdentityScenario.LoginAsync(browser, email, password);
                Assert.Equal(expected, response.StatusCode);
                if (expected == HttpStatusCode.Unauthorized)
                {
                    await ProblemAssert.IsProblemAsync(response, expected, "INVALID_CREDENTIALS");
                    Assert.Null(browser.Cookie("__Host-duora"));
                }
                else
                {
                    Assert.NotNull(browser.Cookie("__Host-duora"));
                }

                return clock.Elapsed;
            }).ToArray();
            start.SetResult();
            var durations = await Task.WhenAll(requests);
            Array.Sort(durations);
            return durations;
        }
        finally
        {
            foreach (var browser in browsers)
            {
                browser.Dispose();
            }
        }
    }

    private sealed class SlowHasher : IPasswordHasher<DuoraUser>
    {
        private readonly PasswordHasher<DuoraUser> _inner = new();
        private int _active;
        private int _maximum;
        private int _verifications;

        public TimeSpan Delay { get; set; }
        public int MaximumConcurrent => _maximum;
        public int Verifications => _verifications;

        public string HashPassword(DuoraUser user, string password) => _inner.HashPassword(user, password);

        public PasswordVerificationResult VerifyHashedPassword(DuoraUser user, string hashedPassword, string providedPassword)
        {
            if (Delay == TimeSpan.Zero)
            {
                return _inner.VerifyHashedPassword(user, hashedPassword, providedPassword);
            }

            Interlocked.Increment(ref _verifications);
            var active = Interlocked.Increment(ref _active);
            int previous;
            do
            {
                previous = Volatile.Read(ref _maximum);
            }
            while (active > previous && Interlocked.CompareExchange(ref _maximum, active, previous) != previous);

            try
            {
                // IPasswordHasher é síncrono. Atraso controlado representa CPU lenta sem saturar o runner inteiro.
                Thread.Sleep(Delay);
                return _inner.VerifyHashedPassword(user, hashedPassword, providedPassword);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }

    /// <summary>Normalizador do Identity que avisa a próxima normalização de e-mail.</summary>
    private sealed class SignallingNormalizer : ILookupNormalizer
    {
        private readonly UpperInvariantLookupNormalizer _inner = new();
        private TaskCompletionSource? _next;

        public Task NextEmailNormalizationAsync()
        {
            var next = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _next, next);
            return next.Task;
        }

        public string? NormalizeEmail(string? email)
        {
            Interlocked.Exchange(ref _next, null)?.TrySetResult();
            return _inner.NormalizeEmail(email);
        }

        public string? NormalizeName(string? name) => _inner.NormalizeName(name);
    }

    /// <summary>Transação cujo encerramento falha, como uma conexão que caiu durante o rollback.</summary>
    private sealed class FailingDisposeTransaction : IDbContextTransaction
    {
        public const string Message = "conexão perdida no rollback";

        public Guid TransactionId { get; } = Guid.NewGuid();

        public void Commit() => throw new NotSupportedException();

        public Task CommitAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public void Rollback() => throw new NotSupportedException();

        public Task RollbackAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public void Dispose() => throw new InvalidOperationException(Message);

        public ValueTask DisposeAsync() => ValueTask.FromException(new InvalidOperationException(Message));
    }
}
