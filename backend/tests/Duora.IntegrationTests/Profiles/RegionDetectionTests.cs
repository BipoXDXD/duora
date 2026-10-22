using System.Net;
using System.Text.Json.Nodes;
using Duora.Infrastructure.Geolocation;
using Duora.IntegrationTests.Identity;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Duora.IntegrationTests.Profiles;

public sealed class RegionDetectionTests(PostgresFixture postgres)
{
    [Fact]
    public async Task DisabledDetectionHasNoCountryAndDoesNotChangePrivateResources()
    {
        await using var factory = new DuoraWebFactory(postgres.ConnectionString);
        var scenario = new IdentityScenario(postgres, factory);
        using var browser = await scenario.SignedInBrowserAsync(await scenario.CreateConfirmedAccountAsync());
        string[] resources = ["profile", "regional-preferences", "preferences", "locale"];
        var before = new List<string>();
        foreach (var resource in resources)
        {
            using var response = await browser.GetAsync($"/api/v1/me/{resource}");
            before.Add(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }
        using var capability = await browser.GetAsync("/api/v1/me/region-detection/capability");
        Assert.Equal(HttpStatusCode.OK, capability.StatusCode);
        Assert.False((bool?)JsonNode.Parse(await capability.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!["enabled"]);
        using var detected = await browser.PostJsonAsync("/api/v1/me/region-detection", null);
        Assert.Equal(HttpStatusCode.OK, detected.StatusCode);
        var body = JsonNode.Parse(await detected.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
        Assert.Equal("disabled", (string?)body["status"]);
        Assert.Null(body["countryCode"]);
        Assert.NotNull(body["observedAt"]);
        Assert.Contains("no-store", detected.Headers.CacheControl!.ToString(), StringComparison.Ordinal);
        for (var i = 0; i < resources.Length; i++)
        {
            using var response = await browser.GetAsync($"/api/v1/me/{resources[i]}");
            Assert.Equal(before[i], await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task DisabledCapabilityDoesNotInvokeTheLookupEvenWithAPublicConnection()
    {
        await using var factory = new DuoraWebFactory(postgres.ConnectionString)
        {
            TestServices = services =>
            {
                RemoteAddressFromTestHeader.Register(services);
                services.AddSingleton<ICountryLookup>(new FailIfUsedLookup());
            },
        };
        var scenario = new IdentityScenario(postgres, factory);
        using var browser = await scenario.SignedInBrowserAsync(await scenario.CreateConfirmedAccountAsync());
        using var response = await browser.PostJsonAsync("/api/v1/me/region-detection", null, RemoteAddressFromTestHeader.From("8.8.8.8"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("disabled", (string?)JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!["status"]);
    }

    [Theory]
    [InlineData("8.8.8.8", null, "BR", "detected")]
    [InlineData("::ffff:8.8.8.8", null, "BR", "detected")]
    [InlineData("2606:4700:4700::1111", null, "BR", "detected")]
    [InlineData("8.8.8.8", "1.1.1.1", "BR", "detected")]
    [InlineData("172.30.10.2", "8.8.8.8", "BR", "detected")]
    [InlineData("172.30.10.2", "1.1.1.1, 8.8.8.8", "BR", "unknown")]
    [InlineData("8.8.8.8", null, "CA", "unsupported")]
    [InlineData("8.8.8.8", null, null, "unknown")]
    [InlineData(null, null, "BR", "unknown")]
    [InlineData("127.0.0.1", null, "BR", "unknown")]
    [InlineData("10.0.0.1", null, "BR", "unknown")]
    [InlineData("100.64.0.1", null, "BR", "unknown")]
    [InlineData("192.0.2.1", null, "BR", "unknown")]
    [InlineData("169.254.169.254", null, "BR", "unknown")]
    [InlineData("::1", null, "BR", "unknown")]
    [InlineData("fc00::1", null, "BR", "unknown")]
    [InlineData("fe80::1", null, "BR", "unknown")]
    [InlineData("2001:db8::1", null, "BR", "unknown")]
    [InlineData("2002:808:808::1", null, "BR", "unknown")]
    public async Task OnlyThePublicConnectionAfterTheTrustedSingleHopCanSuggest(string? remote, string? forwarded, string? country, string status)
    {
        var lookup = new SyntheticLookup(country);
        await using var factory = Factory(lookup);
        var scenario = new IdentityScenario(postgres, factory);
        using var browser = await scenario.SignedInBrowserAsync(await scenario.CreateConfirmedAccountAsync());
        var headers = new Dictionary<string, string>();
        if (remote is not null)
        {
            headers[RemoteAddressFromTestHeader.Header] = remote;
        }

        if (forwarded is not null)
        {
            headers["X-Forwarded-For"] = forwarded;
        }

        using var response = await browser.PostJsonAsync("/api/v1/me/region-detection", null, new MutationHeaders(Extra: headers));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
        Assert.Equal(status, (string?)body["status"]);
        Assert.Equal(status == "detected" ? "BR" : null, (string?)body["countryCode"]);
        if (status == "detected")
        {
            Assert.Equal(remote == "172.30.10.2" ? "8.8.8.8" : remote == "::ffff:8.8.8.8" ? "8.8.8.8" : remote, lookup.Address?.ToString());
        }
        else if (country == "BR")
        {
            Assert.Null(lookup.Address);
        }
    }

    [Fact]
    public async Task AuthenticationCsrfAndBodyAreCheckedWithoutClientSuppliedLookup()
    {
        await using var factory = Factory(new SyntheticLookup("BR"));
        using var visitor = new Browser(factory);
        using var anonymous = await visitor.GetAsync("/api/v1/me/region-detection/capability");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        var scenario = new IdentityScenario(postgres, factory);
        using var browser = await scenario.SignedInBrowserAsync(await scenario.CreateConfirmedAccountAsync());
        using var csrf = await browser.PostJsonAsync("/api/v1/me/region-detection", null, new MutationHeaders(SendCsrfToken: false));
        Assert.Equal(HttpStatusCode.Forbidden, csrf.StatusCode);
        using var body = await browser.PostJsonAsync("/api/v1/me/region-detection", new { ip = "8.8.8.8" });
        Assert.Equal(HttpStatusCode.BadRequest, body.StatusCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RateLimitsHaveNoQueueAndReturnRetryAfterAtNPlusOne(bool accountLimit)
    {
        await using var factory = Factory(new SyntheticLookup("BR"), accountLimit ? 2 : 100, accountLimit ? 100 : 2);
        var scenario = new IdentityScenario(postgres, factory);
        using var first = await scenario.SignedInBrowserAsync(await scenario.CreateConfirmedAccountAsync());
        using var second = await scenario.SignedInBrowserAsync(await scenario.CreateConfirmedAccountAsync());
        for (var i = 0; i < 2; i++)
        {
            using var response = await first.PostJsonAsync("/api/v1/me/region-detection", null, RemoteAddressFromTestHeader.From(accountLimit ? $"8.8.8.{i + 1}" : "8.8.8.8"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        using var limited = await (accountLimit ? first : second).PostJsonAsync("/api/v1/me/region-detection", null,
            RemoteAddressFromTestHeader.From(accountLimit ? "1.1.1.1" : "8.8.8.8"));
        await ProblemAssert.IsProblemAsync(limited, HttpStatusCode.TooManyRequests, "RATE_LIMITED");
        ProblemAssert.RetryAfterSeconds(limited);
    }


    [Theory]
    [InlineData(CountryLookupStatus.Detected, false)]
    [InlineData(CountryLookupStatus.Unknown, false)]
    [InlineData(CountryLookupStatus.Unavailable, false)]
    [InlineData(CountryLookupStatus.Detected, true)]
    public async Task ObservationNeverWritesOrLogsParticipantLocationEvenOnCancellation(CountryLookupStatus status, bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        using var logs = new CapturingLoggerProvider();
        var lookup = new SyntheticLookup("BR", status, cancel ? cancellation.Cancel : null);
        await using var factory = new DuoraWebFactory(postgres.ConnectionString, logs)
        {
            TestServices = services =>
            {
                RemoteAddressFromTestHeader.Register(services);
                services.AddSingleton<ICountryLookup>(lookup);
            },
        };
        var scenario = new IdentityScenario(postgres, factory);
        var account = await scenario.CreateConfirmedAccountAsync();
        using var browser = await scenario.SignedInBrowserAsync(account);
        var before = await SnapshotAsync(account.UserId);
        if (cancel)
        {
            Microsoft.AspNetCore.Http.HttpContext? completed = null;
            var error = await Record.ExceptionAsync(async () => completed = await factory.Server.SendAsync(context =>
            {
                context.RequestAborted = cancellation.Token;
                context.Request.Method = "POST";
                context.Request.Path = "/api/v1/me/region-detection";
                context.Request.Scheme = "https";
                context.Request.Host = new Microsoft.AspNetCore.Http.HostString("localhost");
                context.Request.Headers.Cookie = browser.Cookies.GetCookieHeader(Browser.BaseAddress);
                context.Request.Headers["X-XSRF-TOKEN"] = browser.Cookie("XSRF-TOKEN");
                context.Request.Headers.Origin = DuoraWebFactory.PublicOrigin;
                context.Request.Headers["Sec-Fetch-Site"] = "same-origin";
                context.Request.Headers[RemoteAddressFromTestHeader.Header] = "8.8.8.8";
            }, cancellation.Token));
            Assert.NotNull(lookup.Address);
            Assert.True(cancellation.IsCancellationRequested);
            if (error is null)
            {
                Assert.Equal(499, completed!.Response.StatusCode);
            }
            else
            {
                Assert.IsAssignableFrom<OperationCanceledException>(error);
            }
        }
        else
        {
            using var response = await browser.PostJsonAsync("/api/v1/me/region-detection", null, RemoteAddressFromTestHeader.From("8.8.8.8"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(status.ToString().ToLowerInvariant(), (string?)JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!["status"]);
        }
        Assert.Equal(before, await SnapshotAsync(account.UserId));
        Assert.DoesNotContain(logs.Entries, entry => entry.Text.Contains("8.8.8.8", StringComparison.Ordinal) || System.Text.RegularExpressions.Regex.IsMatch(entry.Text, @"\bBR\b"));
    }

    private async Task<string> SnapshotAsync(Guid userId)
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("""
            select jsonb_build_object(
              'profile', (select to_jsonb(p) from profiles.profiles p where user_id=$1),
              'regional', (select to_jsonb(p) from profiles.regional_preferences p where user_id=$1),
              'languages', (select to_jsonb(p) from profiles.preferences p where user_id=$1),
              'communicationLanguages', (select coalesce(jsonb_agg(to_jsonb(l) order by language_code), '[]'::jsonb) from profiles.communication_languages l where user_id=$1),
              'regionalCountries', (select coalesce(jsonb_agg(to_jsonb(c) order by country_code), '[]'::jsonb) from profiles.regional_preference_countries c where user_id=$1),
              'locale', (select preferred_locale from identity.users where id=$1),
              'guard', (select to_jsonb(g) from safety.user_guards g where user_id=$1),
              'pairs', (select count(*) from events.pairing_participants where user_id=$1))::text
            """, connection);
        command.Parameters.AddWithValue(userId);
        return (string)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private DuoraWebFactory Factory(SyntheticLookup lookup, int accountLimit = 100, int originLimit = 100) => new(postgres.ConnectionString)
    {
        Settings = new Dictionary<string, string?>
        {
            ["ReverseProxy:KnownNetworks:0"] = "172.30.10.0/24",
            ["RateLimiting:RegionDetectionPerAccount:PermitLimit"] = accountLimit.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["RateLimiting:RegionDetectionPerIp:PermitLimit"] = originLimit.ToString(System.Globalization.CultureInfo.InvariantCulture),
        },
        TestServices = services =>
        {
            RemoteAddressFromTestHeader.Register(services);
            services.AddSingleton<ICountryLookup>(lookup);
        },
    };

    private sealed class FailIfUsedLookup : ICountryLookup
    {
        public bool Enabled => false;
        public CountryLookupResult Lookup(IPAddress address) => throw new InvalidOperationException("Disabled lookup must not execute.");
    }

    private sealed class SyntheticLookup(string? country, CountryLookupStatus? status = null, Action? duringLookup = null) : ICountryLookup
    {
        public bool Enabled => true;
        public IPAddress? Address { get; private set; }
        public CountryLookupResult Lookup(IPAddress address)
        {
            Address = address;
            duringLookup?.Invoke();
            return new(status ?? (country is null ? CountryLookupStatus.Unknown : CountryLookupStatus.Detected), country);
        }
    }
}
