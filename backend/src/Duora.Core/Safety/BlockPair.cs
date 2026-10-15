namespace Duora.Core.Safety;

/// <summary>
/// Par de um bloqueio. É direcional no registro (quem bloqueou quem) e bilateral no efeito: <see cref="Separates"/>
/// vale nos dois sentidos (spec 7.3).
/// </summary>
public sealed record BlockPair
{
    public BlockPair(Guid blockerId, Guid blockedId)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(blockerId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(blockedId, Guid.Empty);
        if (blockerId == blockedId)
        {
            throw new ArgumentException("Um par de bloqueio tem duas pessoas diferentes.", nameof(blockedId));
        }

        BlockerId = blockerId;
        BlockedId = blockedId;
    }

    public Guid BlockerId { get; }

    public Guid BlockedId { get; }

    public bool Separates(Guid personA, Guid personB) =>
        (BlockerId == personA && BlockedId == personB) || (BlockerId == personB && BlockedId == personA);
}
