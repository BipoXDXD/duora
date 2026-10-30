namespace Duora.Core.Identity;

/// <summary>Situação das tentativas de selfie de uma conta, calculada das reprovações gravadas (issue #300).</summary>
public abstract record SelfieAttemptStanding
{
    private SelfieAttemptStanding()
    {
    }

    /// <param name="Remaining">Reprovações que ainda cabem no ciclo atual, de 1 a 5.</param>
    /// <param name="LastCycle">Segundo ciclo: esgotá-lo suspende a conta, em vez de pausar por 1 hora.</param>
    public sealed record Available(int Remaining, bool LastCycle) : SelfieAttemptStanding;

    /// <param name="Until">Instante, exclusivo, em que o segundo ciclo abre.</param>
    public sealed record CoolingDown(DateTimeOffset Until) : SelfieAttemptStanding;

    /// <summary>As dez reprovações aconteceram: só a revogação da suspensão por um moderador devolve tentativas.</summary>
    public sealed record Exhausted : SelfieAttemptStanding;
}

/// <summary>
/// Regra do dono de 29/10/2026 (issue #300): primeiro ciclo com cinco tentativas; cinco reprovações pausam a selfie
/// por 1 hora, contada da quinta reprovação; depois, segundo ciclo com mais cinco; a décima reprovação suspende a
/// conta. Conta só o resultado final do fornecedor que reprova a captura: abandono, expiração, indisponibilidade e
/// erro técnico não contam, e a aprovação encerra a contagem.
/// </summary>
public static class SelfieAttemptPolicy
{
    public const int AttemptsPerCycle = 5;

    public const int MaxFailures = AttemptsPerCycle * 2;

    public static readonly TimeSpan CoolDown = TimeSpan.FromHours(1);

    /// <param name="result">Resultado final lido do fornecedor.</param>
    /// <param name="attemptPolicyVersion">Versão da política gravada na tentativa; outra versão é descompasso técnico.</param>
    public static bool CountsAsFailure(SelfieResult result, string attemptPolicyVersion)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.PolicyVersion == attemptPolicyVersion
            && result.State is SelfieState.RecaptureRequired or SelfieState.Inconclusive;
    }

    /// <summary>Verdadeiro só para a reprovação que esgota as tentativas, para pedir a suspensão uma única vez.</summary>
    public static bool ExhaustsAttempts(int failureCount) => failureCount == MaxFailures;

    /// <param name="failures">Instantes das reprovações da conta, em qualquer ordem.</param>
    /// <param name="now">Relógio da aplicação.</param>
    public static SelfieAttemptStanding Evaluate(IReadOnlyCollection<DateTimeOffset> failures, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(failures);
        var count = failures.Count;
        if (count >= MaxFailures)
        {
            return new SelfieAttemptStanding.Exhausted();
        }

        if (count < AttemptsPerCycle)
        {
            return new SelfieAttemptStanding.Available(AttemptsPerCycle - count, LastCycle: false);
        }

        var secondCycleOpensAt = failures.Order().ElementAt(AttemptsPerCycle - 1) + CoolDown;
        return now < secondCycleOpensAt
            ? new SelfieAttemptStanding.CoolingDown(secondCycleOpensAt)
            : new SelfieAttemptStanding.Available(MaxFailures - count, LastCycle: true);
    }
}
