using System.Diagnostics.CodeAnalysis;

namespace Duora.Core.Events;

/// <summary>Resultado de <see cref="Event.CreateDraft"/>: o rascunho válido ou o motivo da recusa.</summary>
public sealed class EventDraftCreation
{
    private EventDraftCreation(Event? draft, EventDraftRefusal? refusal)
    {
        Draft = draft;
        Refusal = refusal;
    }

    public Event? Draft { get; }

    public EventDraftRefusal? Refusal { get; }

    [MemberNotNullWhen(true, nameof(Draft))]
    public bool IsCreated => Draft is not null;

    internal static EventDraftCreation Created(Event draft) => new(draft, null);

    internal static EventDraftCreation Refused(EventDraftRefusal refusal) => new(null, refusal);
}
