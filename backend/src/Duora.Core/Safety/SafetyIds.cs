namespace Duora.Core.Safety;

/// <summary>Protocolo da denúncia (glossário: "Protocolo"). Gerado pelo servidor, nunca pelo cliente.</summary>
public readonly record struct ReportId(Guid Value)
{
    internal static ReportId New(DateTimeOffset now) => new(Guid.CreateVersion7(now));
}

public readonly record struct ModerationActionId(Guid Value)
{
    internal static ModerationActionId New(DateTimeOffset now) => new(Guid.CreateVersion7(now));
}

/// <summary>Protocolo do recurso.</summary>
public readonly record struct AppealId(Guid Value)
{
    internal static AppealId New(DateTimeOffset now) => new(Guid.CreateVersion7(now));
}
