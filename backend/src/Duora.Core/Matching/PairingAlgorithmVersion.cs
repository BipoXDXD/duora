namespace Duora.Core.Matching;

/// <summary>
/// Versão do algoritmo de seleção, persistida com a rodada para que a mesma semente refaça o mesmo resultado
/// (spec 5.1, ADR-0012). Qualquer mudança que altere a saída para a mesma entrada é uma versão nova.
/// </summary>
public enum PairingAlgorithmVersion
{
    /// <summary>Gulosa por prioridade de espera, com desempate pela semente da rodada. Não garante o máximo de pares.</summary>
    GreedyByPriorityV1 = 1,

    /// <summary>Explicit shared communication language and bilateral cross-locale consent.</summary>
    GreedyByPriorityV2 = 2,
}
