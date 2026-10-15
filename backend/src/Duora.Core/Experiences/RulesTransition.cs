namespace Duora.Core.Experiences;

/// <summary>Novo estado produzido pelas regras e se a experiência chegou à sua conclusão.</summary>
public sealed record RulesTransition<TState>(TState NextState, bool IsConcluded)
    where TState : class;
