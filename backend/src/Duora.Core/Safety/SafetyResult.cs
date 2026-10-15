namespace Duora.Core.Safety;

/// <summary>
/// Falha esperada de um comando do Safety: entrada da pessoa ou corrida (duplo clique, estado já mudado). O caso de
/// uso traduz cada uma para a resposta HTTP. Invariante de programação continua sendo exceção (<see cref="SafetyRule"/>).
/// </summary>
public enum SafetyRejection
{
    SelfBlock,
    SelfReport,
    TransitionNotAllowed,

    /// <summary>Quem revisa não pode ser parte do caso nem autor da decisão revista (spec 12.3, TM-34).</summary>
    ReviewerMustBeIndependent,
    OnlyAssignedReviewerCanDecide,

    /// <summary>Classificador não rebaixa denúncia grave; só uma pessoa pode (spec 19.3).</summary>
    AutomatedTriageCannotLowerSevereReport,
    MeasureDurationMismatch,

    /// <summary>Duração acima do teto do autor (<see cref="ModerationLimits"/>).</summary>
    DurationExceedsLimit,
    ActionNotAppealable,
}

/// <summary>
/// Resultado de um comando sem valor de retorno; também fabrica <see cref="SafetyResult{T}"/>, porque membro estático
/// em tipo genérico obriga quem chama a repetir o argumento de tipo (CA1000).
/// </summary>
public readonly record struct SafetyResult
{
    private SafetyResult(SafetyRejection rejection) => Rejection = rejection;

    public SafetyRejection? Rejection { get; }

    public bool IsSuccess => Rejection is null;

    public static SafetyResult Success() => default;

    public static SafetyResult Rejected(SafetyRejection rejection) => new(rejection);

    public static SafetyResult<T> Success<T>(T value)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(value);
        return new SafetyResult<T>(value, rejection: null);
    }

    public static SafetyResult<T> Rejected<T>(SafetyRejection rejection)
        where T : class =>
        new(value: null, rejection);
}

/// <summary>Resultado de um comando que produz <typeparamref name="T"/> quando aceito.</summary>
public sealed class SafetyResult<T>
    where T : class
{
    private readonly T? _value;

    internal SafetyResult(T? value, SafetyRejection? rejection)
    {
        _value = value;
        Rejection = rejection;
    }

    public SafetyRejection? Rejection { get; }

    public bool IsSuccess => Rejection is null;

    /// <summary>Ler o valor de um resultado rejeitado é erro de programação de quem chama.</summary>
    public T Value => _value ?? throw new InvalidOperationException($"Resultado rejeitado: {Rejection}.");
}
