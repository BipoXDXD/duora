namespace Duora.Infrastructure.Outbox;

internal enum OutboxDeliveryStatus
{
    Pending,
    InFlight,
    Delivered,
    Failed,
    Expired,
}

/// <summary>
/// Uma entrega de um evento a um consumidor. Depois de gravada, só muda por SQL condicional no
/// <see cref="OutboxDeliveryQueue"/>, que confere o token de fencing; o EF só a insere.
/// </summary>
internal sealed class OutboxDelivery
{
    private OutboxDelivery()
    {
    }

    public Guid Id { get; private set; }

    public Guid OutboxEventId { get; private set; }

    public string Consumer { get; private set; } = "";

    public string RecipientKey { get; private set; } = "";

    public OutboxDeliveryStatus Status { get; private set; }

    public int Attempts { get; private set; }

    public DateTimeOffset AvailableAt { get; private set; }

    public DateTimeOffset DeadlineAt { get; private set; }

    public DateTimeOffset? LeaseExpiresAt { get; private set; }

    /// <summary>Incrementado a cada posse; só quem tem o valor atual pode concluir a entrega.</summary>
    public long FencingToken { get; private set; }

    /// <summary>Categoria sanitizada da última falha (tipo da exceção), nunca a mensagem.</summary>
    public string? LastError { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public static OutboxDelivery Schedule(
        Guid outboxEventId,
        string consumer,
        string recipientKey,
        DateTimeOffset now,
        DateTimeOffset deadlineAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumer);
        ArgumentException.ThrowIfNullOrWhiteSpace(recipientKey);
        return new OutboxDelivery
        {
            Id = Guid.CreateVersion7(now),
            OutboxEventId = outboxEventId,
            Consumer = consumer,
            RecipientKey = recipientKey,
            Status = OutboxDeliveryStatus.Pending,
            AvailableAt = now,
            DeadlineAt = deadlineAt,
            CreatedAt = now,
        };
    }
}
