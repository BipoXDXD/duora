namespace Duora.Core.Experiences.UnlikelyRoutes;

/// <summary>
/// Parâmetros configuráveis da experiência (spec 4.3). A sessão guarda um snapshot ao nascer, então mudar a
/// configuração não altera sessões em andamento.
/// </summary>
public sealed record UnlikelyRoutesSettings
{
    /// <summary>Teto de revisões aceitável para caber no jogo de até 8 minutos.</summary>
    public const int MaxRevisionsCeiling = 3;

    public UnlikelyRoutesSettings(int maxRevisions)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxRevisions);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxRevisions, MaxRevisionsCeiling);
        MaxRevisions = maxRevisions;
    }

    /// <summary>Quantas vezes, somando os dois participantes, o roteiro pode ser refeito.</summary>
    public int MaxRevisions { get; }
}
