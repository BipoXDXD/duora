using System.Globalization;

namespace Duora.Core.Safety;

/// <summary>
/// Motivo de uma suspensão de conta, num catálogo fechado (coluna <c>reason_code</c> de
/// <c>safety.moderation_actions</c>). A pessoa suspensa vê o código; o motivo interno fica só na auditoria.
/// </summary>
public enum SuspensionReason
{
    /// <summary>Violação dos Termos de Uso, decidida por moderador (issue #299).</summary>
    TermsViolation,

    /// <summary>Reprovação na selfie de presença depois do limite de tentativas (issue #300, regras a decidir).</summary>
    SelfieVerificationFailed,
}

/// <summary>
/// Pedido de suspensão já validado: quem sofre, por quê e quem decide. A suspensão não tem prazo: só uma revogação
/// explícita devolve a conta ao estado anterior (spec 4.2, nenhuma volta a <c>Active</c> por decurso de prazo).
/// </summary>
public sealed record SuspensionOrder
{
    /// <summary>Em caracteres Unicode; mesmo teto de <c>internal_reason</c> no banco.</summary>
    public const int InternalReasonMaxLength = 2000;

    private SuspensionOrder(Guid subjectUserId, SuspensionReason reason, string internalReason, ModerationAuthor author)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(subjectUserId, Guid.Empty);
        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(reason), reason, "Motivo de suspensão desconhecido.");
        }

        SubjectUserId = subjectUserId;
        Reason = reason;
        InternalReason = SafetyText.RequireText(internalReason, InternalReasonMaxLength, nameof(internalReason));
        Author = author;
    }

    public Guid SubjectUserId { get; }

    public SuspensionReason Reason { get; }

    /// <summary>Motivo para auditoria (INV-10); nunca vai para a pessoa suspensa nem para log.</summary>
    public string InternalReason { get; }

    public ModerationAuthor Author { get; }

    public static SuspensionOrder ByModerator(
        Guid moderatorId,
        Guid subjectUserId,
        SuspensionReason reason,
        string internalReason)
    {
        if (moderatorId == subjectUserId)
        {
            throw new ArgumentException("Ninguém decide a própria suspensão.", nameof(subjectUserId));
        }

        return new SuspensionOrder(subjectUserId, reason, internalReason, new ModerationAuthor.Human(moderatorId));
    }

    /// <summary>
    /// Suspensão decidida pelo sistema. Violação dos Termos é julgamento humano (spec 12.3, TM-34) e nunca é
    /// automática; a reprovação na selfie é a exceção prevista para a issue #300.
    /// </summary>
    public static SuspensionOrder Automated(Guid subjectUserId, SuspensionReason reason, string internalReason)
    {
        if (reason == SuspensionReason.TermsViolation)
        {
            throw new SafetyRuleViolationException(SafetyRule.MeasureRequiresHumanAuthor);
        }

        return new SuspensionOrder(subjectUserId, reason, internalReason, new ModerationAuthor.Automated());
    }
}

/// <summary>Revogação de uma suspensão: quem revoga e por quê, para a auditoria.</summary>
public sealed record SuspensionLift
{
    private SuspensionLift(string internalReason, ModerationAuthor author)
    {
        InternalReason = SafetyText.RequireText(internalReason, SuspensionOrder.InternalReasonMaxLength, nameof(internalReason));
        Author = author;
    }

    public string InternalReason { get; }

    public ModerationAuthor Author { get; }

    public static SuspensionLift ByModerator(Guid moderatorId, string internalReason) =>
        new(internalReason, new ModerationAuthor.Human(moderatorId));

    public static SuspensionLift Automated(string internalReason) => new(internalReason, new ModerationAuthor.Automated());
}

/// <summary>
/// Registro do sistema que motivou uma medida automática (a "evidência utilizada" da spec 12.3), só pelo código da
/// origem e pelo id, nunca pelo conteúdo: uma verificação de selfie entra pelo id, sem dado biométrico.
/// </summary>
public sealed record ModerationSource(string Code, Guid Id);

/// <summary>
/// Chave de um comando de moderação: repetir o comando com a mesma chave devolve o mesmo resultado (INV-06). O escopo
/// separa a chave de um moderador da de outro e da de uma origem do sistema (por exemplo, uma verificação de selfie).
/// </summary>
public readonly record struct ModerationCommandKey
{
    private const int MaxKeyLength = 64;
    private const int MaxSourceLength = 31;

    private ModerationCommandKey(string scope, ModerationSource? source)
    {
        Scope = scope;
        Source = source;
    }

    /// <summary>Texto canônico do escopo; quem persiste guarda só o hash dele.</summary>
    public string Scope { get; }

    /// <summary>A origem do comando do sistema; nula no comando de um moderador.</summary>
    public ModerationSource? Source { get; }

    /// <param name="moderatorId">Quem envia o comando, vindo da sessão.</param>
    /// <param name="key">A <c>Idempotency-Key</c> do pedido, em <c>[A-Za-z0-9_-]{1,64}</c>.</param>
    public static ModerationCommandKey ForModerator(Guid moderatorId, string key)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(moderatorId, Guid.Empty);
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length is 0 or > MaxKeyLength || !key.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        {
            throw new ArgumentException("Chave fora do formato.", nameof(key));
        }

        return new ModerationCommandKey(
            string.Create(CultureInfo.InvariantCulture, $"moderator:{moderatorId:D}:{key}"), source: null);
    }

    /// <param name="source">
    /// Código da origem, em letras minúsculas e hífens (<c>selfie-verification</c>), até 31 caracteres: vira o tipo de
    /// alvo da auditoria com <c>_</c> no lugar do hífen.
    /// </param>
    /// <param name="sourceId">Id do registro que motivou o comando; a mesma origem nunca suspende duas vezes.</param>
    public static ModerationCommandKey ForSystem(string source, Guid sourceId)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(sourceId, Guid.Empty);
        var code = SafetyText.RequireCatalogCode(source, MaxSourceLength, nameof(source));
        if (!code.All(c => char.IsAsciiLetterLower(c) || c == '-'))
        {
            throw new ArgumentException("Origem só com letras minúsculas e hífens.", nameof(source));
        }

        return new ModerationCommandKey(
            string.Create(CultureInfo.InvariantCulture, $"system:{code}:{sourceId:D}"),
            new ModerationSource(code, sourceId));
    }
}
