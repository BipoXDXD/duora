namespace Duora.Core.Identity;

/// <summary>
/// Catálogo fixo de papéis (model.md, <c>identity.roles</c>). Conta privilegiada (moderação e administração) é
/// separada da de participante (D13): o banco recusa os dois tipos na mesma conta, e a política da sessão sai daqui.
/// </summary>
public static class AccountRoles
{
    public const string Participant = "participant";
    public const string Moderator = "moderator";
    public const string ProductAdmin = "product_admin";
    public const string Operator = "operator";

    public static IReadOnlyList<string> All { get; } = [Participant, Moderator, ProductAdmin, Operator];

    /// <summary>Papéis com MFA obrigatório e sessão administrativa (ADR-0017).</summary>
    public static IReadOnlyList<string> Privileged { get; } = [Moderator, ProductAdmin, Operator];

    /// <summary>Comparação exata: o nome vem do catálogo, nunca de entrada do usuário.</summary>
    public static bool IsPrivileged(string role) => Privileged.Contains(role, StringComparer.Ordinal);

    public static SessionPolicy SessionPolicyFor(IEnumerable<string> roles) =>
        roles.Any(IsPrivileged) ? SessionPolicy.Administrative : SessionPolicy.Normal;
}
