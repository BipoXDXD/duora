namespace Duora.Core.Events;

/// <summary>
/// Fato produzido por um comando aceito. Quem aplica o comando grava cada efeito na outbox no mesmo commit; o agregado
/// não notifica ninguém (spec 10.4, 10.6).
/// </summary>
public abstract record Effect(DateTimeOffset OccurredOn);

/// <summary>A primeira inscrição da lista de espera ganhou a vaga liberada e deve ser avisada.</summary>
public sealed record WaitlistedRegistrationPromoted(Guid EventId, Guid UserId, DateTimeOffset OccurredOn)
    : Effect(OccurredOn);

/// <summary>Registro de auditoria do cancelamento (spec 8.5): quem, por quê e quando.</summary>
public sealed record EventCancelled(
    Guid EventId,
    Guid CancelledBy,
    CancellationReason Reason,
    DateTimeOffset OccurredOn) : Effect(OccurredOn);

/// <summary>Uma inscrição ativa no evento cancelado; a pessoa recebe o aviso de cancelamento.</summary>
public sealed record CancellationNoticeRequested(Guid EventId, Guid UserId, DateTimeOffset OccurredOn)
    : Effect(OccurredOn);

/// <summary>As reservas de par da rodada devem ser liberadas pelo Matching (<c>released_at</c>).</summary>
public sealed record ReservationsReleaseRequested(Guid EventId, Guid RoundId, DateTimeOffset OccurredOn)
    : Effect(OccurredOn);

/// <summary>
/// Os participantes da rodada recebem um aviso neutro de indisponibilidade, sem motivo individual nem preferência
/// alheia (spec 5.2).
/// </summary>
public sealed record UnavailabilityNoticeRequested(Guid EventId, Guid RoundId, DateTimeOffset OccurredOn)
    : Effect(OccurredOn);
