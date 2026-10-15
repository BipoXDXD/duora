namespace Duora.Core.Matching;

/// <summary>
/// Semente persistida por rodada que desfaz empates de prioridade; nunca é exposta aos participantes (spec 5.1).
/// Qualquer <see cref="long"/> é válido, inclusive zero.
/// </summary>
public readonly record struct RoundSeed(long Value);
