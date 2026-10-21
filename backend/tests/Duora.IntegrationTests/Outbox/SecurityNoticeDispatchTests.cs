using Duora.Core.Notifications;
using Duora.Infrastructure.Email;
using Npgsql;

namespace Duora.IntegrationTests.Outbox;

/// <summary>
/// Leitor dos avisos de mudança de credencial (TM-21), que vai a deploy antes de qualquer gravação deles
/// (expand/contract, model.md): o worker entende os tipos novos, escreve o texto fixo em pt-BR e en com o
/// instante do evento em UTC e recusa aviso que carregue token.
/// </summary>
public sealed class SecurityNoticeDispatchTests(PostgresFixture postgres)
{
    // Futuro em relação ao relógio real, onde o relógio falso do harness começa: ele só anda para a frente.
    private static readonly DateTimeOffset OccurredAt = new(2030, 1, 2, 3, 4, 59, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string, string> NoticeKinds => new()
    {
        { nameof(AccountEmailKind.MfaEnabledNotice), "Subject: Verificação em duas etapas ativada no Duora / Two-step verification turned on for Duora" },
        { nameof(AccountEmailKind.MfaDisabledNotice), "Subject: Verificação em duas etapas desativada no Duora / Two-step verification turned off for Duora" },
        { nameof(AccountEmailKind.RecoveryCodesRegeneratedNotice), "Subject: Novos códigos de recuperação no Duora / New recovery codes for Duora" },
        { nameof(AccountEmailKind.PasswordChangedNotice), "Subject: Senha alterada no Duora / Duora password changed" },
        { nameof(AccountEmailKind.PasswordResetNotice), "Subject: Senha redefinida no Duora / Duora password reset completed" },
    };

    [Theory]
    [MemberData(nameof(NoticeKinds))]
    public async Task EveryNoticeKindIsDeliveredWithTheInstantOfTheEvent(string kind, string subjectLine)
    {
        await using var harness = await EmailOutboxHarness.CreateAsync(postgres, Ct);
        harness.Time.SetUtcNow(OccurredAt);
        var deliveryId = await harness.EnqueueAsync(Enum.Parse<AccountEmailKind>(kind), token: null, "pt-BR", Ct);
        harness.Time.Advance(TimeSpan.FromHours(2));
        var dispatcher = harness.StartWorker();

        await dispatcher.DispatchBatchAsync(Ct);

        Assert.Equal("Delivered", (await harness.ReadDeliveryAsync(deliveryId, Ct)).Status);
        var mail = await File.ReadAllTextAsync(Assert.Single(harness.MailFiles()), Ct);
        Assert.Contains(subjectLine + "\n", mail, StringComparison.Ordinal);
        Assert.Contains("em 2030-01-02 03:04 UTC", mail, StringComparison.Ordinal);
        Assert.Contains("on 2030-01-02 03:04 UTC", mail, StringComparison.Ordinal);
        Assert.Contains($"{EmailOutboxHarness.PublicOrigin}/account\n", mail, StringComparison.Ordinal);
    }

    public static TheoryData<SecurityChange, EmailLanguage> WrittenNotices()
    {
        var data = new TheoryData<SecurityChange, EmailLanguage>();
        foreach (var change in Enum.GetValues<SecurityChange>())
        {
            foreach (var language in Enum.GetValues<EmailLanguage>())
            {
                data.Add(change, language);
            }
        }

        return data;
    }

    /// <summary>
    /// Rollback da release com a gravação para a do leitor: o worker da release anterior lê o payload que o gravador
    /// grava (mesma serialização, sem campo a mais) e entrega. A consulta de drenagem do deploy.md conta o aviso
    /// enquanto ele está na fila e deixa de contar depois da entrega.
    /// </summary>
    [Theory]
    [MemberData(nameof(WrittenNotices))]
    public async Task EveryNoticeTheWriterRecordsIsDeliveredByTheReader(SecurityChange change, EmailLanguage language)
    {
        await using var harness = await EmailOutboxHarness.CreateAsync(postgres, Ct);
        var written = AccountEmailOutbox.CreateSecurityNotice(
            change,
            "titular@example.com",
            Guid.CreateVersion7(),
            language,
            harness.Time.GetUtcNow());
        var deliveryId = await harness.EnqueueAsync(written, Ct);
        Assert.Equal(1, await CountNoticesToDrainAsync(harness));
        var dispatcher = harness.StartWorker();

        await dispatcher.DispatchBatchAsync(Ct);

        Assert.Equal("Delivered", (await harness.ReadDeliveryAsync(deliveryId, Ct)).Status);
        var mail = await File.ReadAllTextAsync(Assert.Single(harness.MailFiles()), Ct);
        Assert.Contains("titular@example.com", mail, StringComparison.Ordinal);
        Assert.Equal(0, await CountNoticesToDrainAsync(harness));
    }

    [Fact]
    public async Task NoticeIsBilingualWhateverTheLanguageOfTheRequest()
    {
        await using var harness = await EmailOutboxHarness.CreateAsync(postgres, Ct);
        harness.Time.SetUtcNow(OccurredAt);
        await harness.EnqueueAsync(AccountEmailKind.PasswordChangedNotice, token: null, "en-US", Ct);
        var dispatcher = harness.StartWorker();

        await dispatcher.DispatchBatchAsync(Ct);

        // Quem recebe pode não ser quem fez a mudança, então o idioma da requisição não decide o aviso.
        var mail = await File.ReadAllTextAsync(Assert.Single(harness.MailFiles()), Ct);
        Assert.Contains("Subject: Senha alterada no Duora / Duora password changed\n", mail, StringComparison.Ordinal);
        Assert.Contains("A senha da sua conta do Duora foi alterada em 2030-01-02 03:04 UTC.", mail, StringComparison.Ordinal);
        Assert.Contains(
            "The password of your Duora account was changed on 2030-01-02 03:04 UTC.",
            mail,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoticeCarryingATokenIsRejectedWithoutSending()
    {
        // Aviso nunca leva credencial: um payload assim é defeito de quem gravou, e o worker não o entrega.
        await using var harness = await EmailOutboxHarness.CreateAsync(postgres, Ct);
        var deliveryId = await harness.EnqueueAsync(AccountEmailKind.MfaDisabledNotice, "tok-1", "pt-BR", Ct);
        var dispatcher = harness.StartWorker();

        await dispatcher.DispatchBatchAsync(Ct);

        var delivery = await harness.ReadDeliveryAsync(deliveryId, Ct);
        Assert.Equal("Failed", delivery.Status);
        Assert.Equal("InvalidPayload", delivery.LastError);
        Assert.Empty(harness.MailFiles());
    }

    [Fact]
    public async Task NoticeStillGoesOutAfterADayOfWorkerOutage()
    {
        // O aviso continua útil por dias (o link de reset, não): a janela de entrega é de 72 h (hipótese).
        await using var harness = await EmailOutboxHarness.CreateAsync(postgres, Ct);
        var deliveryId = await harness.EnqueueAsync(AccountEmailKind.PasswordResetNotice, token: null, "pt-BR", Ct);
        harness.Time.Advance(TimeSpan.FromHours(71));
        var dispatcher = harness.StartWorker();

        await dispatcher.DispatchBatchAsync(Ct);

        Assert.Equal("Delivered", (await harness.ReadDeliveryAsync(deliveryId, Ct)).Status);
    }

    [Fact]
    public async Task NoticeExpiresAfterSeventyTwoHours()
    {
        await using var harness = await EmailOutboxHarness.CreateAsync(postgres, Ct);
        var deliveryId = await harness.EnqueueAsync(AccountEmailKind.PasswordResetNotice, token: null, "pt-BR", Ct);
        harness.Time.Advance(TimeSpan.FromHours(72));
        var dispatcher = harness.StartWorker();

        await dispatcher.DispatchBatchAsync(Ct);

        Assert.Equal("Expired", (await harness.ReadDeliveryAsync(deliveryId, Ct)).Status);
        Assert.Empty(harness.MailFiles());
    }

    /// <summary>A consulta de drenagem dos avisos, igual à do deploy.md (rollback de aplicação).</summary>
    private static async Task<long> CountNoticesToDrainAsync(EmailOutboxHarness harness)
    {
        await using var connection = new NpgsqlConnection(harness.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            """
            select count(*) from ops.outbox_deliveries d
            join ops.outbox_events e on e.id = d.outbox_event_id
            where d.consumer = 'transactional-email' and d.status in ('Pending', 'InFlight')
              and e.payload->>'kind' like '%Notice';
            """,
            connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture);
    }
}
