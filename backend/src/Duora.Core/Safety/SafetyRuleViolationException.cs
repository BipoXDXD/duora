namespace Duora.Core.Safety;

public sealed class SafetyRuleViolationException(SafetyRule rule)
    : Exception($"Regra de segurança violada: {rule}.")
{
    public SafetyRule Rule { get; } = rule;
}
