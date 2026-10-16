using System.Security.Cryptography;
using Duora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Duora.Infrastructure.Operations;

internal enum WebhookReceiptState
{
    /// <summary>Gravado e ainda dentro da transação que o processa.</summary>
    Received,

    /// <summary>O efeito foi aplicado.</summary>
    Processed,

    /// <summary>Autêntico, mas sem efeito: referência desconhecida, nonce errado ou solicitação já decidida.</summary>
    Rejected,
}

/// <summary>
/// Linha de <c>ops.webhook_receipts</c> (model.md), mapeada para a migration. Escrita só por
/// <see cref="WebhookReceipts"/>, na transação de quem processa o callback.
/// </summary>
internal sealed class WebhookReceipt
{
    public const int MaxProviderLength = 32;
    public const int MaxEventIdLength = 200;

    private WebhookReceipt()
    {
    }

    public Guid Id { get; private set; }

    public string Provider { get; private set; } = "";

    public string ProviderEventId { get; private set; } = "";

    public DateTimeOffset ReceivedAt { get; private set; }

    public DateTimeOffset SignatureVerifiedAt { get; private set; }

    public DateTimeOffset? ProcessedAt { get; private set; }

    public byte[] PayloadSha256 { get; private set; } = [];

    /// <summary>Só os campos validados do callback, nunca o corpo cru nem o nonce.</summary>
    public string Payload { get; private set; } = "{}";

    public WebhookReceiptState State { get; private set; }
}

internal sealed class WebhookReceiptConfiguration : IEntityTypeConfiguration<WebhookReceipt>
{
    public void Configure(EntityTypeBuilder<WebhookReceipt> builder)
    {
        var states = string.Join(", ", Enum.GetNames<WebhookReceiptState>().Select(name => $"'{name}'"));
        builder.ToTable("webhook_receipts", DatabaseSchemas.Ops, table =>
        {
            table.HasCheckConstraint("ck_webhook_receipts_state", $"state IN ({states})");
            table.HasCheckConstraint("ck_webhook_receipts_payload_sha256", "octet_length(payload_sha256) = 32");
            table.HasCheckConstraint("ck_webhook_receipts_payload_size", "octet_length(payload::text) <= 65536");
        });
        builder.HasKey(r => r.Id).HasName("pk_webhook_receipts");

        builder.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(r => r.Provider).HasColumnName("provider").HasMaxLength(WebhookReceipt.MaxProviderLength).IsRequired();
        builder.Property(r => r.ProviderEventId).HasColumnName("provider_event_id")
            .HasMaxLength(WebhookReceipt.MaxEventIdLength).IsRequired();
        builder.Property(r => r.ReceivedAt).HasColumnName("received_at");
        builder.Property(r => r.SignatureVerifiedAt).HasColumnName("signature_verified_at");
        builder.Property(r => r.ProcessedAt).HasColumnName("processed_at");
        builder.Property(r => r.PayloadSha256).HasColumnName("payload_sha256").IsRequired();
        builder.Property(r => r.Payload).HasColumnName("payload").HasColumnType("jsonb").IsRequired();
        builder.Property(r => r.State).HasColumnName("state").HasConversion<string>().HasMaxLength(16);

        // Unicidade do evento do fornecedor (spec 10.3): é o que torna o callback repetido inofensivo.
        builder.HasIndex(r => new { r.Provider, r.ProviderEventId })
            .IsUnique()
            .HasDatabaseName("ux_webhook_receipts_provider_provider_event_id");
        builder.HasIndex(r => r.ReceivedAt).HasDatabaseName("ix_webhook_receipts_received_at");
    }
}

/// <summary>
/// Recibos de webhook, dono Operations. Como a outbox, quem processa grava o recibo na própria transação
/// (modules.md, nota 4): a unicidade de <c>(provider, provider_event_id)</c> serializa callbacks repetidos, e o
/// recibo some junto se o efeito voltar atrás.
/// </summary>
internal static class WebhookReceipts
{
    /// <summary>Grava o recibo; nulo quando o mesmo evento já foi recebido (callback repetido).</summary>
    public static async Task<Guid?> TryRecordAsync(
        DuoraDbContext db,
        string provider,
        string eventId,
        ReadOnlyMemory<byte> body,
        string payloadJson,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        RequireTransaction(db);
        var id = Guid.CreateVersion7(now);
        var hash = SHA256.HashData(body.Span);
        var received = nameof(WebhookReceiptState.Received);
        var rows = await db.Database.SqlQuery<Guid>(
            $"""
            insert into ops.webhook_receipts
                (id, provider, provider_event_id, received_at, signature_verified_at, payload_sha256, payload, state)
            values ({id}, {provider}, {eventId}, {now}, {now}, {hash}, cast({payloadJson} as jsonb), {received})
            on conflict (provider, provider_event_id) do nothing

            returning id as "Value"
            """).ToListAsync(cancellationToken);
        return rows.Count == 1 ? rows[0] : null;
    }

    public static async Task SettleAsync(
        DuoraDbContext db,
        Guid receiptId,
        WebhookReceiptState state,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        RequireTransaction(db);
        var name = state.ToString();
        var received = nameof(WebhookReceiptState.Received);
        await db.Database.ExecuteSqlAsync(
            $"""
            update ops.webhook_receipts set state = {name}, processed_at = {now}
            where id = {receiptId} and state = {received}
            """,
            cancellationToken);
    }

    private static void RequireTransaction(DuoraDbContext db)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Recibo de webhook só é gravado na transação do processamento.");
        }
    }
}
