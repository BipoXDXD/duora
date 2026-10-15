namespace Duora.Core.Matching;

/// <summary>Resultado da seleção de uma rodada, ainda sem reserva: quem monta a transação revalida e reserva (spec 5.2).</summary>
public sealed record PairSelection
{
    internal PairSelection(
        IReadOnlyList<UserPair> pairs,
        IReadOnlyList<Guid> unpaired,
        UnpairedTally unpairedTally,
        PairingAlgorithmVersion algorithmVersion)
    {
        Pairs = pairs;
        Unpaired = unpaired;
        UnpairedTally = unpairedTally;
        AlgorithmVersion = algorithmVersion;
    }

    /// <summary>Pares na ordem em que foram formados (prioridade decrescente de quem iniciou o par).</summary>
    public IReadOnlyList<UserPair> Pairs { get; }

    /// <summary>Quem ficou sem par, em prioridade decrescente. Sem motivo individual: ele revelaria preferências alheias.</summary>
    public IReadOnlyList<Guid> Unpaired { get; }

    public UnpairedTally UnpairedTally { get; }

    public PairingAlgorithmVersion AlgorithmVersion { get; }
}

/// <summary>
/// Contagem agregada de por que pessoas ficaram sem par, para as métricas por evento e versão do algoritmo (spec 5.3).
/// Só faz sentido agregada e com supressão de grupos pequenos; não ligue a contagem a uma pessoa.
/// </summary>
/// <param name="WithoutCompatiblePartner">Sem nenhuma aresta viável na rodada.</param>
/// <param name="CompatiblePartnersTaken">Tinha aresta viável, mas todos os possíveis pares foram formados com outras pessoas.</param>
public sealed record UnpairedTally(int WithoutCompatiblePartner, int CompatiblePartnersTaken);
