using System.Net;
using Duora.IntegrationTests.Identity;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using static Duora.Infrastructure.Participation.ParticipationSql;

namespace Duora.IntegrationTests.Participation;

/// <summary>
/// T-28 e T-06 no caminho real da inscrição: PUT por HTTP, com cookie e CSRF, sobre o PostgreSQL. Os testes de domínio
/// de <c>RegistrationBook</c> não passam por este SQL.
/// </summary>
public sealed class RegistrationSingleEffectTests(PostgresFixture postgres)
{
    private const string Password = IdentityScenario.ValidPassword;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false, "Confirmed|-", "1|1|2")]
    [InlineData(true, "Waitlisted|1", "1|2|3")]
    public async Task RepeatedRegistrationKeepsOneSeatAndOneCount(bool eventIsFull, string expectedRegistration, string expectedEvent)
    {
        await using var scenario = await Scenario.CreateAsync(postgres);
        using var alice = await scenario.SignInAsync(scenario.Seed.Alice, "alice@example.com");
        using var bob = await scenario.SignInAsync(scenario.Seed.Bob, "bob@example.com");
        if (eventIsFull)
        {
            await scenario.RegisterAsync(alice);
        }

        await scenario.RegisterAsync(bob);
        var afterTheFirstRequest = await scenario.RegistrationRowAsync(scenario.Seed.Bob);
        scenario.Clock.Advance(TimeSpan.FromSeconds(1));
        await scenario.RegisterAsync(bob);

        Assert.Equal(afterTheFirstRequest, await scenario.RegistrationRowAsync(scenario.Seed.Bob));
        Assert.Equal(expectedRegistration, await scenario.RegistrationAsync(scenario.Seed.Bob));
        Assert.Equal(expectedEvent, await scenario.EventCountersAsync());
        Assert.Equal(eventIsFull ? 2 : 1, await scenario.RegistrationCountAsync());
    }

    /// <summary>
    /// Segura a linha do evento até as duas requisições estarem na fila dela, para o resultado depender da serialização
    /// e não de uma chegada em momentos diferentes. Sem o lock da inscrição, as duas leem a mesma vaga e param no UPDATE
    /// do contador: a segunda fura a capacidade.
    /// </summary>
    [Fact]
    public async Task TwoPeopleRacingForTheLastSeatConfirmOneAndWaitlistTheOther()
    {
        await using var scenario = await Scenario.CreateAsync(postgres);
        using var alice = await scenario.SignInAsync(scenario.Seed.Alice, "alice@example.com");
        using var bob = await scenario.SignInAsync(scenario.Seed.Bob, "bob@example.com");
        await using var blocker = await scenario.Source.OpenConnectionAsync(Ct);
        await using var hold = await blocker.BeginTransactionAsync(Ct);
        await Execute(blocker, "select 1 from events.events where id=$1 for update", Ct, scenario.EventId);

        var requests = new[] { alice.PutAsync(scenario.RegistrationPath), bob.PutAsync(scenario.RegistrationPath) };
        await scenario.WaitForBlockedBackendsAsync(requests.Length);
        await hold.CommitAsync(Ct);
        var responses = await Task.WhenAll(requests);

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.NoContent, response.StatusCode));
        foreach (var response in responses)
        {
            response.Dispose();
        }

        string[] registrations =
        [
            await scenario.RegistrationAsync(scenario.Seed.Alice),
            await scenario.RegistrationAsync(scenario.Seed.Bob),
        ];
        Assert.Equal(["Confirmed|-", "Waitlisted|1"], registrations.Order(StringComparer.Ordinal));
        Assert.Equal("1|2|3", await scenario.EventCountersAsync());
    }

    private sealed class Scenario : IAsyncDisposable
    {
        private readonly DuoraWebFactory _factory;
        private readonly IdentityScenario _identity;

        private Scenario(ParticipationSeed seed, FakeTimeProvider clock, DuoraWebFactory factory, IdentityScenario identity,
            NpgsqlDataSource source)
        {
            Seed = seed;
            Clock = clock;
            _factory = factory;
            _identity = identity;
            Source = source;
        }

        public ParticipationSeed Seed { get; }

        public FakeTimeProvider Clock { get; }

        public NpgsqlDataSource Source { get; }

        public Guid EventId => Seed.Events[0];

        public string RegistrationPath => $"/api/v1/events/{EventId}/registration";

        public static async Task<Scenario> CreateAsync(PostgresFixture postgres)
        {
            var connectionString = await postgres.CreateMigratedDatabaseAsync(Ct);
            var now = DateTimeOffset.UtcNow;
            var seed = await BrowserScenarioSeed.InitializeAsync(connectionString, now, Ct, declarePreferences: true);
            var clock = new FakeTimeProvider(now);
            var factory = new DuoraWebFactory(connectionString) { Time = clock };
            var source = NpgsqlDataSource.Create(connectionString);
            await using var connection = await source.OpenConnectionAsync(Ct);
            await Execute(connection, "update events.events set capacity=1 where id=$1", Ct, seed.Events[0]);
            return new(seed, clock, factory, new IdentityScenario(connectionString, factory), source);
        }

        public Task<Browser> SignInAsync(Guid user, string email) => _identity.SignedInBrowserAsync(new(user, email, Password));

        public async Task RegisterAsync(Browser browser)
        {
            using var response = await browser.PutAsync(RegistrationPath);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        public Task<string> RegistrationAsync(Guid user) => TextAsync(
            "select state||'|'||coalesce(waitlist_position::text,'-') from events.registrations where event_id=$1 and user_id=$2",
            EventId, user);

        /// <summary>A linha inteira que o reenvio não pode alterar, inclusive datas e versão.</summary>
        public Task<string> RegistrationRowAsync(Guid user) => TextAsync(
            "select r::text from events.registrations r where event_id=$1 and user_id=$2", EventId, user);

        /// <summary><c>confirmed_count|next_waitlist_position|version</c>.</summary>
        public Task<string> EventCountersAsync() => TextAsync(
            "select confirmed_count||'|'||next_waitlist_position||'|'||version from events.events where id=$1", EventId);

        public async Task<long> RegistrationCountAsync()
        {
            await using var connection = await Source.OpenConnectionAsync(Ct);
            await using var query = Command(connection, "select count(*) from events.registrations where event_id=$1", EventId);
            return (long)(await query.ExecuteScalarAsync(Ct))!;
        }

        public async Task WaitForBlockedBackendsAsync(int expected)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await using var connection = await Source.OpenConnectionAsync(timeout.Token);
            await using var query = Command(connection, """
                select count(*)::integer from pg_stat_activity
                where datname=current_database() and cardinality(pg_blocking_pids(pid))>0
                """);
            while ((int)(await query.ExecuteScalarAsync(timeout.Token))! < expected)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Source.DisposeAsync();
            await _factory.DisposeAsync();
        }

        private async Task<string> TextAsync(string sql, params object[] values)
        {
            await using var connection = await Source.OpenConnectionAsync(Ct);
            await using var query = Command(connection, sql, values);
            return (string)(await query.ExecuteScalarAsync(Ct))!;
        }
    }
}
