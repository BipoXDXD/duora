namespace Duora.Core.Safety;

/// <summary>
/// Bloqueio pedido por uma pessoa. Aplica-se na hora, sem gate humano (spec 12.3). Repetir o bloqueio é
/// idempotente pela unicidade do par no banco.
/// </summary>
public sealed class Block
{
    private Block(BlockPair pair, DateTimeOffset createdAt)
    {
        Pair = pair;
        CreatedAt = createdAt;
    }

    public BlockPair Pair { get; }

    public DateTimeOffset CreatedAt { get; }

    public static Block Create(Guid blockerId, Guid blockedId, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return new Block(new BlockPair(blockerId, blockedId), clock.GetUtcNow());
    }

    /// <summary>
    /// Desbloqueio: só remove o bloqueio. Não restaura conexão, conversa, decisão nem sala (spec 7.3, TM-27),
    /// e por isso o fato devolvido não carrega nada além do par e do instante.
    /// </summary>
    public BlockLifted Lift(Guid requesterId, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (requesterId != Pair.BlockerId)
        {
            throw new SafetyRuleViolationException(SafetyRule.OnlyBlockerCanLift);
        }

        return new BlockLifted(Pair.BlockerId, Pair.BlockedId, clock.GetUtcNow());
    }
}
