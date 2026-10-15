namespace Duora.Infrastructure.Identity;

internal enum LegalDocument
{
    Terms,
    Privacy,
}

/// <summary>Aceite de uma versão de documento legal no cadastro (spec 10.2, <c>consent_records</c>).</summary>
internal sealed class ConsentRecord
{
    private ConsentRecord()
    {
    }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public LegalDocument Document { get; private set; }

    public string Version { get; private set; } = "";

    public DateTimeOffset AcceptedAt { get; private set; }

    public static ConsentRecord Accept(Guid userId, LegalDocument document, string version, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        return new ConsentRecord
        {
            Id = Guid.CreateVersion7(now),
            UserId = userId,
            Document = document,
            Version = version,
            AcceptedAt = now,
        };
    }
}
