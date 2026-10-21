using System.Text.Json;
using Duora.Core.Notifications;
using Duora.Infrastructure.Email;

namespace Duora.IntegrationTests.Outbox;

public sealed class PreferredTimeZoneDispatchTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("America/New_York", "01/01/2030, 22:04 — America/New_York · UTC−05:00", "1/1/30, 10:04 PM — America/New_York · UTC−05:00")]
    [InlineData("America/Sao_Paulo", "02/01/2030, 00:04 — America/Sao_Paulo · UTC−03:00", "1/2/30, 12:04 AM — America/Sao_Paulo · UTC−03:00")]
    [InlineData("UTC", null, null)]
    [InlineData(null, null, null)]
    public async Task WorkerUsesOnlyTheEnqueuedChoiceAndKeepsUtc(string? zone, string? pt, string? en)
    {
        await using var harness = await EmailOutboxHarness.CreateAsync(postgres, Ct);
        harness.Time.SetUtcNow(new DateTimeOffset(2030, 1, 2, 3, 4, 59, TimeSpan.Zero));
        var message = AccountEmailOutbox.CreateSecurityNotice(SecurityChange.PasswordChanged, "ana@example.com", Guid.CreateVersion7(), EmailLanguage.English, harness.Time.GetUtcNow(), zone);
        using (var json = JsonDocument.Parse(message.Payload))
        {
            if (zone is null)
            {
                Assert.False(json.RootElement.TryGetProperty("preferredTimeZone", out _));
            }
            else
            {
                Assert.Equal(zone, json.RootElement.GetProperty("preferredTimeZone").GetString());
            }
        }
        var id = await harness.EnqueueAsync(message, Ct);
        harness.Time.Advance(TimeSpan.FromHours(2));
        await harness.StartWorker().DispatchBatchAsync(Ct);
        Assert.Equal("Delivered", (await harness.ReadDeliveryAsync(id, Ct)).Status);
        var mail = await File.ReadAllTextAsync(Assert.Single(harness.MailFiles()), Ct);
        Assert.Contains("em 2030-01-02 03:04 UTC.", mail, StringComparison.Ordinal);
        Assert.Contains("on 2030-01-02 03:04 UTC.", mail, StringComparison.Ordinal);
        if (pt is null)
        {
            Assert.DoesNotContain("No seu fuso salvo:", mail, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("No seu fuso salvo: " + pt, mail, StringComparison.Ordinal);
        }
        if (en is null)
        {
            Assert.DoesNotContain("In your saved time zone:", mail, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("In your saved time zone: " + en, mail, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("Mars/Olympus", nameof(AccountEmailKind.PasswordChangedNotice))]
    [InlineData("Eastern Standard Time", nameof(AccountEmailKind.PasswordChangedNotice))]
    [InlineData("UTC", nameof(AccountEmailKind.EmailConfirmation))]
    public async Task InvalidOrUnrelatedZoneFailsClosed(string zone, string kind)
    {
        await using var harness = await EmailOutboxHarness.CreateAsync(postgres, Ct);
        var message = new AccountEmailRequested(Enum.Parse<AccountEmailKind>(kind), "ana@example.com", Guid.CreateVersion7(), kind == nameof(AccountEmailKind.EmailConfirmation) ? "token" : null, "pt-BR", zone);
        var id = await harness.EnqueueAsync(AccountEmailOutbox.Create(message, harness.Time.GetUtcNow()), Ct);
        await harness.StartWorker().DispatchBatchAsync(Ct);
        var delivery = await harness.ReadDeliveryAsync(id, Ct);
        Assert.Equal("Failed", delivery.Status);
        Assert.Equal("InvalidPayload", delivery.LastError);
        Assert.Empty(harness.MailFiles());
    }
}
