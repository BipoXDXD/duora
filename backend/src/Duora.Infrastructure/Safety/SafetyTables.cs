namespace Duora.Infrastructure.Safety;

/// <summary>
/// Linha de `safety.user_guards` (model.md): serializa as operações sobre uma pessoa (ordem 1 da spec 10.4) e
/// carrega a versão de segurança. Nunca representa consentimento. Mapeada para a migration; a escrita é SQL do
/// <see cref="BlockService"/>.
/// </summary>
internal sealed class UserGuard
{
    private UserGuard()
    {
    }

    public Guid UserId { get; private set; }

    /// <summary>Sobe a cada bloqueio que envolve a pessoa; no M2, também por sanção e preferência.</summary>
    public long SecurityVersion { get; private set; }
}

/// <summary>Linha de `safety.blocks`: direcional no registro, bilateral no efeito (spec 7.3).</summary>
internal sealed class StoredBlock
{
    private StoredBlock()
    {
    }

    public Guid Id { get; private set; }

    public Guid BlockerId { get; private set; }

    public Guid BlockedId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
}
