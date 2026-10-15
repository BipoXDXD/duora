namespace Duora.Core.Safety;

/// <summary>
/// INV-03: decide se duas pessoas podem interagir agora. Vale igual para novo pareamento, envio de mensagem e
/// leituras protegidas, porque a spec não diferencia o efeito do bloqueio entre elas. Quem chama revalida dentro
/// da transação, depois de trancar as guardas do par (TM-23).
/// </summary>
public static class InteractionPolicy
{
    /// <param name="personA">Uma das pessoas; a ordem do par não altera a resposta.</param>
    /// <param name="personB">A outra pessoa.</param>
    /// <param name="blocks">Bloqueios que envolvem o par; os de outros pares são ignorados.</param>
    /// <param name="sanctions">
    /// Histórico de medidas das duas pessoas, com as revisões de recurso, para que uma medida substituída deixe
    /// de valer.
    /// </param>
    /// <param name="clock">Relógio do instante da checagem.</param>
    public static InteractionVerdict CheckInteraction(
        Guid personA,
        Guid personB,
        IReadOnlyCollection<Block> blocks,
        IReadOnlyCollection<ModerationAction> sanctions,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentNullException.ThrowIfNull(sanctions);
        ArgumentNullException.ThrowIfNull(clock);
        if (personA == personB)
        {
            throw new ArgumentException("Interação exige duas pessoas diferentes.", nameof(personB));
        }

        var blocked = blocks.Any(block => block.Pair.Separates(personA, personB));
        var restricted = HasRestrictionInForce(personA, personB, sanctions, clock.GetUtcNow());
        return blocked || restricted ? InteractionVerdict.Unavailable : InteractionVerdict.Allowed;
    }

    private static bool HasRestrictionInForce(
        Guid personA,
        Guid personB,
        IReadOnlyCollection<ModerationAction> sanctions,
        DateTimeOffset now)
    {
        var superseded = sanctions
            .Select(action => action.SupersedesActionId)
            .OfType<ModerationActionId>()
            .ToHashSet();

        return sanctions
            .Where(action => action.SubjectUserId == personA || action.SubjectUserId == personB)
            .Where(action => !superseded.Contains(action.Id))
            .Any(action => action.RestrictsInteractionAt(now));
    }
}
