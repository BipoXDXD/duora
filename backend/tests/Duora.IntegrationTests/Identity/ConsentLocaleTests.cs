using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Duora.IntegrationTests.Identity;

public sealed class ConsentLocaleTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("pt-BR", "en-US")]
    [InlineData("en-US", "pt-BR")]
    public async Task RegistrationKeepsEachDocumentLocaleIndependentOfAccountPreference(string terms, string privacy)
    {
        var publication = SyntheticLegalPublication.Create("terms-v2", "privacy-v3");
        await using var factory = new DuoraWebFactory(postgres.ConnectionString) { LegalDocuments = publication };
        var scenario = new IdentityScenario(postgres, factory);
        using var browser = new Browser(factory);
        var email = IdentityScenario.NewEmail();
        await browser.FetchCsrfTokenAsync();
        using var response = await browser.PostJsonAsync("/api/v1/auth/register", new
        {
            email,
            password = IdentityScenario.ValidPassword,
            acceptedTermsVersion = "terms-v2",
            acceptedPrivacyVersion = "privacy-v3",
            acceptedTermsLocale = terms,
            acceptedPrivacyLocale = privacy,
            acceptedTermsSha256 = SyntheticLegalPublication.Hash(publication, "terms", terms),
            acceptedPrivacySha256 = SyntheticLegalPublication.Hash(publication, "privacy", privacy),
        });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var proof = await scenario.ReadLatestLinkAsync(email, "EmailConfirmation");
        using var confirmed = await browser.PostJsonAsync("/api/v1/auth/confirm-email", new
        {
            userId = proof.UserId,
            token = proof.Token,
            password = IdentityScenario.ValidPassword,
        });
        Assert.Equal(HttpStatusCode.NoContent, confirmed.StatusCode);
        using var signedIn = await scenario.SignedInBrowserAsync(new TestAccount(proof.UserId, email, IdentityScenario.ValidPassword));
        var before = await ReadEvidenceAsync(postgres.ConnectionString, proof.UserId);
        foreach (var locale in new[] { "pt-BR", "en-US" })
        {
            using var changed = await signedIn.PutAsync("/api/v1/me/locale", $$"""{"locale":"{{locale}}"}""",
                new MutationHeaders(Extra: new Dictionary<string, string> { ["If-Match"] = locale == "pt-BR" ? "\"0\"" : "\"1\"" }));
            Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        }
        // A new registration cannot rewrite the evidence of a confirmed account either.
        using var repeated = await browser.PostJsonAsync("/api/v1/auth/register", IdentityScenario.RegistrationBody(email, publication: publication));
        Assert.Equal(HttpStatusCode.Accepted, repeated.StatusCode);
        Assert.Equal(before, await ReadEvidenceAsync(postgres.ConnectionString, proof.UserId));
        Assert.Equal(1, await scenario.ScalarAsync(
            "select count(*) from identity.consent_records where user_id = $1 and document = 'Terms' and version = 'terms-v2' and document_locale = $2", proof.UserId, terms));
        Assert.Equal(1, await scenario.ScalarAsync(
            "select count(*) from identity.consent_records where user_id = $1 and document = 'Privacy' and version = 'privacy-v3' and document_locale = $2", proof.UserId, privacy));
    }

    [Theory]
    [InlineData("pt-br")]
    [InlineData("en")]
    [InlineData("es-ES")]
    [InlineData("")]
    [InlineData(" pt-BR")]
    [InlineData("en-US ")]
    [InlineData("pt-BR\u0000")]
    public async Task InvalidLocaleIsRejectedBeforeAnyAccountOrOutboxWrite(string locale)
    {
        await using var factory = new DuoraWebFactory(postgres.ConnectionString);
        var scenario = new IdentityScenario(postgres, factory);
        using var browser = new Browser(factory);
        await browser.FetchCsrfTokenAsync();
        foreach (var invalidTerms in new[] { true, false })
        {
            var email = IdentityScenario.NewEmail();
            using var response = await browser.PostJsonAsync("/api/v1/auth/register", new
            {
                email,
                password = IdentityScenario.ValidPassword,
                acceptedTermsVersion = "v1",
                acceptedPrivacyVersion = "v1",
                acceptedTermsLocale = invalidTerms ? locale : "pt-BR",
                acceptedPrivacyLocale = invalidTerms ? "en-US" : locale,
            });
            var problem = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
            Assert.Equal("INVALID_FORMAT", (string?)problem["errors"]?[invalidTerms ? "acceptedTermsLocale" : "acceptedPrivacyLocale"]?[0]);
            Assert.Equal(0, await scenario.CountUsersAsync(email));
            Assert.Equal(0, await scenario.CountOutboxEventsAsync(email));
        }
    }

    [Fact]
    public async Task MissingDocumentLocalesCannotAuthorizeANewRegistration()
    {
        await using var factory = new DuoraWebFactory(postgres.ConnectionString);
        var scenario = new IdentityScenario(postgres, factory);
        using var browser = new Browser(factory);
        var email = IdentityScenario.NewEmail();
        await browser.FetchCsrfTokenAsync();
        using var registered = await browser.PostJsonAsync("/api/v1/auth/register", new
        {
            email,
            password = IdentityScenario.ValidPassword,
            acceptedTermsVersion = IdentityScenario.TermsVersion,
            acceptedPrivacyVersion = IdentityScenario.TermsVersion,
        });
        await ProblemAssert.IsProblemAsync(registered, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.Equal(0, await scenario.CountUsersAsync(email));
        Assert.Equal(0, await scenario.CountOutboxEventsAsync(email));
    }

    [Fact]
    public async Task AdditiveMigrationPreservesLegacyEvidenceAndOldSqlReaders()
    {
        var database = await postgres.CreateEmptyDatabaseAsync(Ct);
        await using (var before = PostgresFixture.CreateDbContext(database.MigratorConnectionString))
        {
            var migrations = before.Database.GetMigrations().ToArray();
            // The predecessor is found by name so later migrations do not change this upgrade test.
            var index = Array.FindIndex(migrations, m => m.EndsWith("_ConsentDocumentLocale", StringComparison.Ordinal));
            Assert.True(index > 0, "ConsentDocumentLocale migration must exist");
            await before.GetService<IMigrator>().MigrateAsync(migrations[index - 1], cancellationToken: Ct);
        }
        await using var connection = new NpgsqlConnection(database.AppConnectionString);
        await connection.OpenAsync(Ct);
        var userId = await RoleSeparationTests.InsertUserAsync(connection);
        await using var command = new NpgsqlCommand("""
            insert into identity.consent_records (id, user_id, document, version, accepted_at)
            values ($1, $2, 'Terms', 'legacy', '2026-01-01Z')
            """, connection);
        var id = Guid.NewGuid();
        command.Parameters.AddWithValue(id);
        command.Parameters.AddWithValue(userId);
        await command.ExecuteNonQueryAsync(Ct);
        await using (var after = PostgresFixture.CreateDbContext(database.MigratorConnectionString))
        {
            await after.Database.MigrateAsync(Ct);
        }
        command.CommandText = "select count(*) from identity.consent_records where id = $1 and user_id = $2 and document = 'Terms' and version = 'legacy' and accepted_at = '2026-01-01Z' and document_locale is null and document_sha256 is null";
        Assert.Equal(1L, await command.ExecuteScalarAsync(Ct));
        command.CommandText = "select version from identity.consent_records where id = $1 and user_id = $2";
        Assert.Equal("legacy", await command.ExecuteScalarAsync(Ct));
    }

    [Fact]
    public async Task FingerprintRollbackWaitsForWritersBeforeCheckingForEvidence()
    {
        var database = await postgres.CreateEmptyDatabaseAsync(Ct);
        string previousMigration;
        await using (var setup = PostgresFixture.CreateDbContext(database.MigratorConnectionString))
        {
            await setup.Database.MigrateAsync(Ct);
            var migrations = setup.Database.GetMigrations().ToArray();
            var index = Array.FindIndex(migrations, migration => migration.EndsWith("_ConsentDocumentFingerprint", StringComparison.Ordinal));
            Assert.True(index > 0);
            previousMigration = migrations[index - 1];
        }
        await using var writer = new NpgsqlConnection(database.AppConnectionString);
        await writer.OpenAsync(Ct);
        var userId = await RoleSeparationTests.InsertUserAsync(writer);
        await using var transaction = await writer.BeginTransactionAsync(Ct);
        await using (var insert = new NpgsqlCommand("""
            insert into identity.consent_records (id, user_id, document, version, accepted_at, document_locale, document_sha256)
            values ($1, $2, 'Terms', 'synthetic', now(), 'pt-BR', $3)
            """, writer, transaction))
        {
            insert.Parameters.AddWithValue(Guid.NewGuid());
            insert.Parameters.AddWithValue(userId);
            insert.Parameters.AddWithValue(new string('a', 64));
            await insert.ExecuteNonQueryAsync(Ct);
        }
        await using var rollbackContext = PostgresFixture.CreateDbContext(database.MigratorConnectionString);
        var rollback = rollbackContext.GetService<IMigrator>().MigrateAsync(previousMigration, cancellationToken: Ct);
        await using var observer = new NpgsqlConnection(database.MigratorConnectionString);
        await observer.OpenAsync(Ct);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (true)
        {
            await using var waiting = new NpgsqlCommand("select count(*) from pg_stat_activity where $1 = any(pg_blocking_pids(pid))", observer);
            waiting.Parameters.AddWithValue(writer.ProcessID);
            if ((long)(await waiting.ExecuteScalarAsync(Ct))! > 0)
            {
                break;
            }
            Assert.False(rollback.IsCompleted, "Rollback must wait for the writer before deciding whether evidence exists.");
            Assert.True(DateTimeOffset.UtcNow < deadline, "Rollback did not wait for the pending consent writer.");
            await Task.Delay(TimeSpan.FromMilliseconds(20), Ct);
        }
        await transaction.CommitAsync(Ct);
        var error = await Assert.ThrowsAsync<PostgresException>(() => rollback);
        Assert.Contains("Cannot remove recorded legal document fingerprints", error.MessageText, StringComparison.Ordinal);
        await using var evidence = new NpgsqlCommand("select count(*) from identity.consent_records where user_id = $1 and document_sha256 = $2", observer);
        evidence.Parameters.AddWithValue(userId);
        evidence.Parameters.AddWithValue(new string('a', 64));
        Assert.Equal(1L, await evidence.ExecuteScalarAsync(Ct));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("pt-BR")]
    [InlineData("en-US")]
    public async Task DatabaseRejectsChangingEvidenceButAllowsAccountDeletion(string? locale)
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync(Ct);
        var userId = await RoleSeparationTests.InsertUserAsync(connection);
        await using var command = new NpgsqlCommand("""
            insert into identity.consent_records (id, user_id, document, version, accepted_at, document_locale)
            values ($1, $2, 'Terms', 'v1', now(), $3)
            """, connection);
        var id = Guid.NewGuid();
        command.Parameters.AddWithValue(id);
        command.Parameters.AddWithValue(userId);
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text, Value = (object?)locale ?? DBNull.Value });
        await command.ExecuteNonQueryAsync(Ct);
        await using (var context = postgres.CreateDbContext())
        {
            var record = await context.Set<Duora.Infrastructure.Identity.ConsentRecord>().SingleAsync(c => c.Id == id, Ct);
            Assert.Equal(locale, record.DocumentLocale);
            Assert.Equal("v1", record.Version);
        }
        command.Parameters.Clear();
        command.Parameters.AddWithValue(id);
        foreach (var assignment in new[] { "document_locale = null", "document_locale = 'pt-BR'", "document_locale = 'en-US'", "version = 'v2'", "accepted_at = accepted_at + interval '1 second'", "document = 'Privacy'" })
        {
            // No-op updates are permitted; any changed evidence, including unknown -> known, is refused.
            command.CommandText = $"update identity.consent_records set {assignment} where id = $1";
            if ((locale is null && assignment == "document_locale = null") || assignment == $"document_locale = '{locale}'")
            {
                Assert.Equal(1, await command.ExecuteNonQueryAsync(Ct));
                continue;
            }
            var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync(Ct));
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        }
        command.Parameters.Clear();
        command.Parameters.AddWithValue(userId);
        command.CommandText = "delete from identity.users where id = $1";
        Assert.Equal(1, await command.ExecuteNonQueryAsync(Ct));
        command.CommandText = "select count(*) from identity.consent_records where user_id = $1";
        Assert.Equal(0L, await command.ExecuteScalarAsync(Ct));
    }

    [Theory]
    [InlineData("pt-br")]
    [InlineData("en")]
    [InlineData("es-ES")]
    [InlineData("")]
    [InlineData("en-US ")]
    public async Task DatabaseRejectsInvalidLocaleEvenOutsideHttp(string locale)
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync(Ct);
        var userId = await RoleSeparationTests.InsertUserAsync(connection);
        await using var command = new NpgsqlCommand("""
            insert into identity.consent_records (id, user_id, document, version, accepted_at, document_locale)
            values ($1, $2, 'Terms', 'v1', now(), $3)
            """, connection);
        command.Parameters.AddWithValue(Guid.NewGuid());
        command.Parameters.AddWithValue(userId);
        command.Parameters.AddWithValue(locale);
        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync(Ct));
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        Assert.Equal("ck_consent_records_document_locale", error.ConstraintName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OutboxFailureRollsBackAccountAndBothConsents(bool replacePending)
    {
        var database = await postgres.CreateEmptyDatabaseAsync(Ct);
        await using (var context = PostgresFixture.CreateDbContext(database.MigratorConnectionString))
        {
            await context.Database.MigrateAsync(Ct);
        }
        await using var factory = new DuoraWebFactory(database.AppConnectionString);
        var scenario = new IdentityScenario(database.AppConnectionString, factory);
        using var browser = new Browser(factory);
        var email = IdentityScenario.NewEmail();
        string? evidence = null;
        Guid? userId = null;
        if (replacePending)
        {
            using var initial = await IdentityScenario.RegisterAsync(browser, email);
            Assert.Equal(HttpStatusCode.Accepted, initial.StatusCode);
            userId = (await scenario.ReadLatestLinkAsync(email, "EmailConfirmation")).UserId;
            evidence = await ReadEvidenceAsync(database.AppConnectionString, userId.Value);
        }
        await using var connection = new NpgsqlConnection(database.MigratorConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("""
            create function ops.fail_test_outbox() returns trigger language plpgsql as $$
            begin raise exception 'Synthetic outbox failure' using errcode = '23514'; end $$;
            create trigger fail_test_outbox before insert on ops.outbox_events
            for each row execute function ops.fail_test_outbox();
            """, connection);
        await command.ExecuteNonQueryAsync(Ct);
        await browser.FetchCsrfTokenAsync();
        using var failed = await browser.PostJsonAsync("/api/v1/auth/register", IdentityScenario.RegistrationBody(email, "replacement password long enough", privacyLocale: "en-US"));
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Equal(replacePending ? 1 : 0, await scenario.CountUsersAsync(email));
        Assert.Equal(replacePending ? 1 : 0, await scenario.CountOutboxEventsAsync(email));
        if (userId is not null)
        {
            Assert.Equal(evidence, await ReadEvidenceAsync(database.AppConnectionString, userId.Value));
            var proof = await scenario.ReadLatestLinkAsync(email, "EmailConfirmation");
            using var confirmed = await browser.PostJsonAsync("/api/v1/auth/confirm-email", new
            {
                userId = proof.UserId,
                token = proof.Token,
                password = IdentityScenario.ValidPassword,
            });
            Assert.Equal(HttpStatusCode.NoContent, confirmed.StatusCode);
        }
        else
        {
            Assert.Equal(0, await scenario.ScalarAsync("select count(*) from identity.consent_records"));
        }
    }

    [Fact]
    public async Task ConcurrentPendingRegistrationsKeepVersionAndLocalePairsFromOneTransaction()
    {
        await using var factory = new DuoraWebFactory(postgres.ConnectionString);
        var scenario = new IdentityScenario(postgres, factory);
        var email = IdentityScenario.NewEmail();
        using var first = new Browser(factory);
        using var second = new Browser(factory);
        await first.FetchCsrfTokenAsync();
        await second.FetchCsrfTokenAsync();
        var responses = await Task.WhenAll(
            first.PostJsonAsync("/api/v1/auth/register", IdentityScenario.RegistrationBody(email, termsLocale: "pt-BR", privacyLocale: "pt-BR")),
            second.PostJsonAsync("/api/v1/auth/register", IdentityScenario.RegistrationBody(email, termsLocale: "en-US", privacyLocale: "en-US")));
        foreach (var response in responses)
        {
            using (response)
            {
                Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            }
        }
        var proof = await scenario.ReadLatestLinkAsync(email, "EmailConfirmation");
        Assert.Equal(1, await scenario.CountUsersAsync(email));
        Assert.Equal(2, await scenario.ScalarAsync("select count(*) from identity.consent_records where user_id = $1", proof.UserId));
        var firstWon = await scenario.ScalarAsync("select count(*) from identity.consent_records where user_id = $1 and version = '2026-10-01' and document_locale = 'pt-BR' and document_sha256 is not null", proof.UserId);
        var secondWon = await scenario.ScalarAsync("select count(*) from identity.consent_records where user_id = $1 and version = '2026-10-01' and document_locale = 'en-US' and document_sha256 is not null", proof.UserId);
        Assert.True((firstWon == 2 && secondWon == 0) || (firstWon == 0 && secondWon == 2));
    }

    private static async Task<string> ReadEvidenceAsync(string connectionString, Guid userId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("select jsonb_agg(to_jsonb(c) order by document)::text from identity.consent_records c where user_id = $1", connection);
        command.Parameters.AddWithValue(userId);
        return (string)(await command.ExecuteScalarAsync(Ct))!;
    }
}
