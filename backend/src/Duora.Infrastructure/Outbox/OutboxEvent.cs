namespace Duora.Infrastructure.Outbox;

/// <summary>
/// Fato registrado na mesma transação do efeito de negócio (spec 10.6), com payload mínimo e versionado.
/// Quem consome são as entregas: uma por consumidor e destinatário lógico.
/// </summary>
internal sealed class OutboxEvent
{
    private readonly List<OutboxDelivery> _deliveries = [];

    private OutboxEvent()
    {
    }

    public Guid Id { get; private set; }

    public string Type { get; private set; } = "";

    public int SchemaVersion { get; private set; }

    public string ResourceType { get; private set; } = "";

    public Guid ResourceId { get; private set; }

    /// <summary>Versão do recurso quando ele tem uma; contas do Identity não têm versão numérica.</summary>
    public long? ResourceVersion { get; private set; }

    /// <summary>JSON do evento. Esvaziado quando todas as entregas terminam, porque pode carregar token de uso único.</summary>
    public string Payload { get; private set; } = "{}";

    public DateTimeOffset OccurredAt { get; private set; }

    public IReadOnlyCollection<OutboxDelivery> Deliveries => _deliveries.AsReadOnly();

    public static OutboxEvent Record(EventIdentity identity, string payload, DateTimeOffset occurredAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        return new OutboxEvent
        {
            Id = Guid.CreateVersion7(occurredAt),
            Type = identity.Type,
            SchemaVersion = identity.SchemaVersion,
            ResourceType = identity.ResourceType,
            ResourceId = identity.ResourceId,
            Payload = payload,
            OccurredAt = occurredAt,
        };
    }

    public void AddDelivery(string consumer, string recipientKey, DateTimeOffset deadlineAt)
    {
        if (deadlineAt <= OccurredAt)
        {
            throw new ArgumentOutOfRangeException(nameof(deadlineAt), deadlineAt, "O prazo deve ser posterior ao evento.");
        }

        _deliveries.Add(OutboxDelivery.Schedule(Id, consumer, recipientKey, OccurredAt, deadlineAt));
    }
}

/// <summary>Tipo, versão de schema e recurso de um evento da outbox.</summary>
internal sealed record EventIdentity(string Type, int SchemaVersion, string ResourceType, Guid ResourceId);
