namespace Duora.Core.Safety;

/// <summary>Estados de uma unidade do manifesto (ADR-0049); um estado terminal só nasce de <see cref="Pending"/>.</summary>
public enum ReconciliationUnitState
{
    Pending = 1,
    Applied,
    SkippedChanged,
    NeedsReview,
}

/// <summary>Uma inscrição do sujeito relida sob locks: o previsto agora, nos termos do manifesto, e a classificação.</summary>
public sealed record ReconciliationCurrentItem(ReconciliationPreviewItem Forecast, LegacyRegistrationClassification Classification);

/// <summary>A unidade relida sob locks: a medida ativa da conta e todas as inscrições dela.</summary>
public sealed record ReconciliationCurrentUnit(LegacyMeasureFacts? ActiveMeasure, IReadOnlyList<ReconciliationCurrentItem> Items);

/// <summary>Desfecho terminal sem retirada: o estado do recibo e o motivo fechado.</summary>
public sealed record ReconciliationUnitRefusal(ReconciliationUnitState State, string Reason);

/// <summary>
/// Passo 9 do protocolo da reconciliação (ADR-0049): compara o manifesto com o estado sob locks. Só decide; não lê nem
/// grava. Mudanças de capacidade ou da fila de terceiros não mudam as versões comparadas: quem as pega são os tetos.
/// </summary>
public static class ReconciliationUnitRevalidation
{
    /// <summary>A medida ativa não é mais a do manifesto, na mesma versão.</summary>
    public const string MeasureChanged = nameof(MeasureChanged);

    /// <summary>Uma inscrição aprovada sumiu ou tem outra versão ou outro estado.</summary>
    public const string RegistrationChanged = nameof(RegistrationChanged);

    /// <summary>O teto de liberação ou de promoção do manifesto é menor que o previsto agora.</summary>
    public const string CeilingExceeded = nameof(CeilingExceeded);

    /// <summary>Plano completo acima dos tetos de eventos ou de usuários; decidido antes das guardas (passo 5).</summary>
    public const string PlanTooLarge = nameof(PlanTooLarge);

    /// <summary>Nulo quando a unidade pode seguir; senão, o desfecho a gravar no recibo.</summary>
    public static ReconciliationUnitRefusal? Decide(
        Guid originalMeasureId,
        long measureVersion,
        IReadOnlyList<ReconciliationPreviewItem> manifest,
        ReconciliationCurrentUnit current)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(current);
        if (current.ActiveMeasure is not { } measure || measure.Id != originalMeasureId || measure.Version != measureVersion)
        {
            return Skipped(MeasureChanged);
        }

        var approved = manifest
            .Select(item => (Manifest: item, Current: current.Items.FirstOrDefault(found => found.Forecast.EventId == item.EventId)))
            .ToArray();
        if (approved.Any(pair => !IsSameRegistration(pair.Manifest, pair.Current)))
        {
            return Skipped(RegistrationChanged);
        }

        if (current.Items.Any(item => item.Classification.Issues.Contains(LegacyRegistrationIssue.ActiveReservation)))
        {
            return new ReconciliationUnitRefusal(ReconciliationUnitState.NeedsReview, nameof(LegacyRegistrationIssue.ActiveReservation));
        }

        var stopped = current.Items.FirstOrDefault(item => item.Classification.BlocksUnit)
            ?? approved.Select(pair => pair.Current!).FirstOrDefault(item => !item.Classification.IsCandidate);
        if (stopped is not null)
        {
            return Skipped(stopped.Classification.Primary!.Value.ToString());
        }

        return approved.Any(pair => IsAboveCeiling(pair.Manifest, pair.Current!.Forecast))
            ? new ReconciliationUnitRefusal(ReconciliationUnitState.NeedsReview, CeilingExceeded)
            : null;
    }

    private static ReconciliationUnitRefusal Skipped(string reason) => new(ReconciliationUnitState.SkippedChanged, reason);

    private static bool IsSameRegistration(ReconciliationPreviewItem manifest, ReconciliationCurrentItem? current) =>
        current is not null && current.Forecast.RegistrationVersion == manifest.RegistrationVersion
        && current.Forecast.State == manifest.State;

    private static bool IsAboveCeiling(ReconciliationPreviewItem manifest, ReconciliationPreviewItem forecast) =>
        forecast.MaxReleasedSeats > manifest.MaxReleasedSeats || forecast.MaxPromotions > manifest.MaxPromotions;
}
