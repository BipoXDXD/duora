namespace Duora.Core.Media;

/// <summary>Estados de um upload de foto (spec 4.2). Só <see cref="Approved"/> pode ser lido por alguém.</summary>
public enum UploadState
{
    /// <summary>Registro criado e vaga reservada; os bytes ainda estão chegando.</summary>
    Pending,

    /// <summary>Original guardado na quarentena e processamento pedido na outbox.</summary>
    Quarantined,

    /// <summary>O Worker tomou o processamento.</summary>
    Processing,

    Approved,

    Rejected,

    Deleted,

    /// <summary>Derivação concluída; aguarda decisão humana. Leitores preparados antes dos escritores (ADR-0035).</summary>
    AwaitingReview,
}

/// <summary>Transições permitidas da máquina de estados do upload (spec 4.2 e 16.2).</summary>
public static class UploadLifecycle
{
    /// <summary>Estados que ocupam vaga no teto de pendentes por conta (spec 11.5).</summary>
    public static IReadOnlyList<UploadState> PendingStates { get; } =
        [UploadState.Pending, UploadState.Quarantined, UploadState.Processing, UploadState.AwaitingReview];

    /// <summary>
    /// <c>Processing → Processing</c> é a retomada por outro worker depois que o lease do primeiro venceu. Exclusão
    /// (API-M03 e limpeza de abandonados) vale a partir de qualquer estado que ainda não seja <c>Deleted</c>.
    /// </summary>
    public static bool CanMove(UploadState from, UploadState to) => (from, to) switch
    {
        (UploadState.Deleted, _) => false,
        (_, UploadState.Deleted) => true,
        (UploadState.Pending, UploadState.Quarantined) => true,
        (UploadState.Quarantined, UploadState.Processing) => true,
        (UploadState.Processing, UploadState.Processing or UploadState.Approved or UploadState.Rejected) => true,
        _ => false,
    };

    public static bool CountsAsPending(UploadState state) => PendingStates.Contains(state);
}
