using System.Net;
using Duora.Core.Media;
using Duora.Core.Safety;
using Duora.Infrastructure.Operations;
using Duora.Infrastructure.Safety;
using Duora.IntegrationTests.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using static Duora.Infrastructure.Participation.ParticipationSql;

namespace Duora.IntegrationTests.Safety;

/// <summary>
/// Legado sintético pronto para aplicar (ADR-0049): capacidade 1, Alice confirmada com suspensão anterior à política, Bob
/// e Third na espera, e um run aberto vindo da prévia real. As corridas chamam o coordenador por uma conexão com
/// <c>application_name</c> próprio, para a barreira por <c>pg_blocking_pids</c> achar quem está esperando.
/// </summary>
internal sealed class ReconciliationApplyScenario : IAsyncDisposable
{
    public const string Path = RegistrationReconciliationRunHttpTests.Path;

    // Fora de identity (a sessão é tocada) e de safety (as guardas nascem sob demanda): AccountsAsync cobre o resto dos dois.
    public const string BusinessSchemas = "'profiles','events','experiences','social','media','privacy'";

    private ReconciliationApplyScenario(SuspendedRegistrationScenario s, DuoraWebFactory host, AccountSuspensionScenario accounts,
        Guid moderatorId, Browser moderator, Guid sessionId)
    {
        S = s;
        Host = host;
        Accounts = accounts;
        ModeratorId = moderatorId;
        Moderator = moderator;
        Session = new ModeratorSession(moderatorId, sessionId);
    }

    public SuspendedRegistrationScenario S { get; }

    public DuoraWebFactory Host { get; }

    public AccountSuspensionScenario Accounts { get; }

    public Guid ModeratorId { get; }

    public Browser Moderator { get; }

    public ModeratorSession Session { get; }

    public Guid Run { get; private set; }

    public string RunTag { get; private set; } = string.Empty;

    public IReadOnlyDictionary<Guid, Guid> Units { get; private set; } = new Dictionary<Guid, Guid>();

    public IReadOnlyDictionary<Guid, Guid> Measures { get; private set; } = new Dictionary<Guid, Guid>();

    /// <summary>O que o coordenador logou nas chamadas diretas de <see cref="ApplyAsync"/>.</summary>
    public CapturingLoggerProvider CoordinatorLogs { get; } = new();

    /// <summary>O que o host web logou.</summary>
    public CapturingLoggerProvider HostLogs { get; private init; } = new();

    public Guid Unit => Units[S.Seed.Alice];

    public Guid Measure => Measures[S.Seed.Alice];

    public ReconciliationCutoverEvidence? Evidence =>
        Host.Services.GetRequiredService<IOptions<RegistrationReconciliationCutoverOptions>>().Value.Evidence();

    public CancellationToken Token => S.Token;

    /// <summary>
    /// O cenário, antes do run. <paramref name="arrange"/> roda depois das inscrições e antes da prévia; o run nasce com
    /// <paramref name="subjects"/>, Alice se vazio.
    /// </summary>
    public static async Task<ReconciliationApplyScenario> CreateAsync(PostgresFixture postgres, Guid[]? subjects = null,
        Func<SuspendedRegistrationScenario, Task>? arrange = null, bool thirdWaits = true, int capacity = 1, bool createRun = true,
        Func<SuspendedRegistrationScenario, Task<Guid[]>>? pick = null, TimeSpan? statementTimeout = null)
    {
        var s = await SuspendedRegistrationScenario.CreateAsync(postgres, capacity);
        await s.ExecuteAsync("update events.events set starts_at=$2 where id=$1", s.EventId, s.Clock.GetUtcNow().AddHours(1));
        await s.RegisterAsync(s.Seed.Alice);
        await s.RegisterAsync(s.Seed.Bob);
        if (thirdWaits)
        {
            await s.RegisterAsync(s.Seed.Third);
        }

        if (arrange is not null)
        {
            await arrange(s);
        }

        var hostLogs = new CapturingLoggerProvider();
        var host = RegistrationReconciliationPreviewEffectsTests.Host(s, statementTimeout, hostLogs);
        var accounts = new AccountSuspensionScenario(s.Database.AppConnectionString, host, s.Clock);
        var (account, moderator) = await accounts.PrivilegedBrowserAsync();
        var session = await MfaScenario.ReadSessionIdAsync(moderator);
        var scenario = new ReconciliationApplyScenario(s, host, accounts, account.UserId, moderator, session) { HostLogs = hostLogs };
        var chosen = pick is not null ? await pick(s) : subjects ?? [s.Seed.Alice];
        if (createRun)
        {
            await scenario.CreateRunAsync(chosen);
        }

        return scenario;
    }

    public async Task CreateRunAsync(params Guid[] subjects)
    {
        Dictionary<Guid, Guid> measures = [];
        foreach (var subject in subjects)
        {
            measures[subject] = await RegistrationReconciliationPreviewEffectsTests.SuspendBeforePolicyAsync(S, subject);
        }

        Measures = measures;
        using var preview = await Moderator.GetAsync(RegistrationReconciliationPreviewHttpTests.Path);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var page = (await MfaScenario.ReadObjectAsync(preview))["page"]!.AsArray();
        Assert.Equal(subjects.Length, page.Count);
        var body = new
        {
            units = page.Select(unit => new
            {
                subjectUserId = (string?)unit!["subjectUserId"],
                originalMeasureId = (string?)unit["originalMeasureId"],
                unitDigest = (string?)unit["unitDigest"],
            }).ToArray(),
            justification = "Prévia revisada pelo moderador.",
            authorizationReference = "AUTH-321-1",
        };
        using var created = await Moderator.PostJsonAsync(Path, body, AccountSuspensionScenario.WithKey(AccountSuspensionScenario.NewKey()));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var run = await MfaScenario.ReadObjectAsync(created);
        Run = Guid.Parse((string)run["id"]!);
        RunTag = (string)run["eTag"]!;
        Units = run["units"]!.AsArray().ToDictionary(
            unit => Guid.Parse((string)unit!["subjectUserId"]!), unit => Guid.Parse((string)unit!["id"]!));
    }

    public string ApplyPath(Guid? unit = null, Guid? run = null) => $"{Path}/{run ?? Run}/units/{unit ?? Unit}:apply";

    public string ClosePath(Guid? run = null) => $"{Path}/{run ?? Run}:close";

    public Task<HttpResponseMessage> PostApplyAsync(Guid? unit = null, string? key = null, Browser? browser = null) =>
        (browser ?? Moderator).PostJsonAsync(ApplyPath(unit), null, AccountSuspensionScenario.WithKey(key ?? AccountSuspensionScenario.NewKey()));

    public Task<HttpResponseMessage> PostCloseAsync(string? tag = null, Browser? browser = null) =>
        (browser ?? Moderator).PostJsonAsync(ClosePath(), null, AccountSuspensionScenario.WithIfMatch(tag ?? RunTag));

    public Task<ReconciliationApplyOutcome> ApplyAsync(Guid? unit = null, string? key = null, string application = "reconciliation-apply",
        ModeratorSession? session = null, int maxPlanEvents = RegistrationReconciliationCoordinator.MaxPlanEvents,
        int maxPlanUsers = RegistrationReconciliationCoordinator.MaxPlanUsers, ReconciliationCutoverEvidence? evidence = null) =>
        ApplyRunAsync(Run, unit ?? Unit, key, application, session, maxPlanEvents, maxPlanUsers, evidence);

    public async Task<ReconciliationApplyOutcome> ApplyRunAsync(Guid run, Guid unit, string? key = null, string application = "reconciliation-apply",
        ModeratorSession? session = null, int maxPlanEvents = RegistrationReconciliationCoordinator.MaxPlanEvents,
        int maxPlanUsers = RegistrationReconciliationCoordinator.MaxPlanUsers, ReconciliationCutoverEvidence? evidence = null)
    {
        var connectionString = new NpgsqlConnectionStringBuilder(S.Database.AppConnectionString)
        { ApplicationName = application }.ConnectionString;
        await using var named = NpgsqlDataSource.Create(connectionString);
        var previews = new LegacyRegistrationReconciliationPreviews(named, S.Clock);
        using var logging = LoggerFactory.Create(builder => builder.AddProvider(CoordinatorLogs));
        var coordinator = new RegistrationReconciliationCoordinator(named, S.Clock, previews,
            logging.CreateLogger<RegistrationReconciliationCoordinator>(), maxPlanEvents, maxPlanUsers);
        Assert.True(IdempotencyKey.TryParse(key ?? AccountSuspensionScenario.NewKey(), out var parsed));
        return await coordinator.ApplyAsModeratorAsync(session ?? Session, run, unit, evidence ?? Evidence, parsed, Token);
    }

    public async Task<ReconciliationRunCloseOutcome> CloseAsync(string application = "reconciliation-close", long expectedVersion = 1,
        ModeratorSession? session = null)
    {
        var connectionString = new NpgsqlConnectionStringBuilder(S.Database.AppConnectionString)
        { ApplicationName = application }.ConnectionString;
        await using var named = NpgsqlDataSource.Create(connectionString);
        var runs = new RegistrationReconciliationRuns(named, S.Clock, new LegacyRegistrationReconciliationPreviews(named, S.Clock),
            NullLogger<RegistrationReconciliationRuns>.Instance);
        return await runs.CloseAsModeratorAsync(session ?? Session, Run, expectedVersion, Token);
    }

    public Task<string> UnitStateAsync(Guid? unit = null) =>
        S.TextAsync("select state from ops.registration_reconciliation_units where id=$1", unit ?? Unit);

    public async Task<string> FingerprintAsync() =>
        await RegistrationReconciliationPreviewEffectsTests.FingerprintAsync(S, BusinessSchemas, includeSequences: false)
        + await AccountsAsync();

    /// <summary>Contas, medidas e versão de segurança: nada disso está no alcance de uma unidade (ADR-0049).</summary>
    public Task<string> AccountsAsync() => S.TextAsync("""
        select md5((select coalesce(string_agg(u::text,',' order by u.id),'') from identity.users u)
          ||(select coalesce(string_agg(m::text,',' order by m.id),'') from safety.moderation_actions m)
          ||(select coalesce(sum(security_version),0)::text from safety.user_guards))
        """);

    public async Task AssertNoEffectAsync(string before, Guid? unit = null)
    {
        Assert.Equal(before, await FingerprintAsync());
        Assert.Equal("Pending", await UnitStateAsync(unit));
        Assert.Equal(0, await S.CountAsync("select count(*) from ops.registration_reconciliation_unit_facts"));
        Assert.Equal(0, await S.CountAsync("select count(*) from ops.audit_events where action like 'events.legacy_%'"));
    }

    /// <summary>Roda DDL ou SQL com o papel do migrador: gatilhos de falha só deste banco avulso.</summary>
    public async Task AsMigratorAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(S.Database.MigratorConnectionString);
        await connection.OpenAsync(Token);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Token);
    }

    /// <summary>
    /// Conta as tentativas da transação numa sequência, que não volta atrás com o rollback: a guarda da pessoa é pedida uma
    /// vez por tentativa, mesmo quando a linha já existe, porque o gatilho BEFORE INSERT roda antes do ON CONFLICT.
    /// </summary>
    public Task CountAttemptsAsync(Guid guardedUser) => AsMigratorAsync($"""
        create sequence ops.test_attempts;
        grant usage, select on sequence ops.test_attempts to duora_app;
        create function ops.test_count_attempt() returns trigger language plpgsql as $f$
        begin perform nextval('ops.test_attempts'); return new; end $f$;
        create trigger test_count_attempt before insert on safety.user_guards for each row
          when (new.user_id = '{guardedUser}') execute function ops.test_count_attempt();
        """);

    public Task<long> AttemptsAsync() => S.CountAsync("select case when is_called then last_value else 0 end from ops.test_attempts");

    /// <summary>Segura uma linha até o teste decidir: o ponto exato em que a aplicação vai esperar.</summary>
    public async Task<Holder> HoldAsync(string sql, params object[] values)
    {
        var connection = await S.Source.OpenConnectionAsync(Token);
        var transaction = await connection.BeginTransactionAsync(Token);
        await Execute(connection, sql, Token, values);
        return new Holder(connection, transaction, Token);
    }

    public Task<Holder> HoldEventAsync() => HoldAsync("select 1 from events.events where id=$1 for update", S.EventId);

    public async Task<Holder> HoldGuardAsync(Guid user)
    {
        await S.ExecuteAsync("insert into safety.user_guards(user_id) values($1) on conflict do nothing", user);
        return await HoldAsync("select 1 from safety.user_guards where user_id=$1 for update", user);
    }

    public async ValueTask DisposeAsync()
    {
        Moderator.Dispose();
        await Host.DisposeAsync();
        await S.DisposeAsync();
    }

    internal sealed class Holder(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token) : IAsyncDisposable
    {
        public NpgsqlConnection Connection => connection;

        public Task ExecuteAsync(string sql, params object[] values) => Execute(connection, sql, token, values);

        public async Task CommitAsync() => await transaction.CommitAsync(token);

        public async Task RollbackAsync() => await transaction.RollbackAsync(token);

        public async ValueTask DisposeAsync()
        {
            await transaction.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
