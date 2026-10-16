using Duora.Core.Identity;

namespace Duora.Infrastructure.Identity;

/// <summary>
/// Sessão de login com o ticket de autenticação protegido (spec 10.2, <c>login_sessions</c> + <c>auth_tickets</c>,
/// fundidas porque são 1:1 e têm a mesma vida). Mapeada para o schema; leitura e escrita são SQL direto em
/// <see cref="LoginSessions"/>, porque validar, expirar e deslizar precisa ser um comando atômico.
/// </summary>
internal sealed class LoginSession
{
    private LoginSession()
    {
    }

    /// <summary>Identificador público, mostrado em <c>/me/sessions</c>. Não autentica nada.</summary>
    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    /// <summary>SHA-256 da chave opaca do cookie; a chave em si nunca é gravada.</summary>
    public byte[] KeyHash { get; private set; } = [];

    /// <summary>Ticket serializado e protegido com Data Protection.</summary>
    public byte[] Ticket { get; private set; } = [];

    /// <summary>Security stamp da conta no login; se a conta mudar de stamp, a sessão deixa de valer.</summary>
    public string SecurityStamp { get; private set; } = "";

    public string Description { get; private set; } = "";

    /// <summary>Política decidida no login pelos papéis da conta; fixa a janela ociosa e a absoluta.</summary>
    public SessionPolicy Policy { get; private set; }

    /// <summary>Última prova de credencial nesta sessão (login ou API-A13), para o step-up.</summary>
    public DateTimeOffset? ReauthenticatedAt { get; private set; }

    /// <summary>Última prova de segundo fator nesta sessão; nula numa sessão aberta só com senha.</summary>
    public DateTimeOffset? MfaVerifiedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset LastSeenAt { get; private set; }

    public DateTimeOffset IdleExpiresAt { get; private set; }

    public DateTimeOffset AbsoluteExpiresAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public string? RevocationReason { get; private set; }
}
