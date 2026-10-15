namespace Duora.Core.Matching;

/// <summary>Semente persistida por rodada que desfaz empates de prioridade; nunca é exposta aos participantes (spec 5.1).</summary>
public sealed record RoundSeed(long Value);
