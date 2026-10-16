using Duora.Core.Identity;
using Microsoft.AspNetCore.Identity;

namespace Duora.Infrastructure.Identity;

/// <summary>
/// Conta do Identity com o estado do produto (spec 4.2). O nome de usuário é o próprio e-mail: não há apelido de
/// login, e a unicidade do e-mail normalizado é garantida por índice único. A conta nasce por upsert em SQL no
/// <see cref="AccountService.RegisterAsync"/>, que precisa substituir um cadastro pendente no mesmo comando.
/// </summary>
public sealed class DuoraUser : IdentityUser<Guid>
{
    /// <summary>Só para o EF materializar a entidade.</summary>
    private DuoraUser()
    {
    }

    public AccountStatus Status { get; private set; }

    /// <summary>Criação da conta; num cadastro pendente substituído, o instante do cadastro que ficou.</summary>
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>
    /// Último passo TOTP (RFC 6238) aceito para esta conta. Um código só vale se o passo dele for maior: o mesmo
    /// código, ou um anterior ainda dentro da tolerância, é recusado. Gravado só por UPDATE condicional em
    /// <see cref="AccountSecurityService"/>.
    /// </summary>
    public long? LastTotpStep { get; private set; }
}
