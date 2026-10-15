using Duora.Core.Identity;
using Microsoft.AspNetCore.Identity;

namespace Duora.Infrastructure.Identity;

/// <summary>
/// Conta do Identity com o estado do produto (spec 4.2). O nome de usuário é o próprio e-mail: não há apelido de
/// login, e a unicidade do e-mail normalizado é garantida por índice único.
/// </summary>
public sealed class DuoraUser : IdentityUser<Guid>
{
    /// <summary>Só para o EF materializar a entidade.</summary>
    private DuoraUser()
    {
    }

    public AccountStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    internal static DuoraUser Register(EmailAddress email, DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(now),
            UserName = email.Value,
            Email = email.Value,
            Status = AccountStatus.PendingEmail,
            CreatedAt = now,
        };

    /// <summary>E-mail confirmado: a conta passa a depender só da elegibilidade (verificação etária, spec 12.2).</summary>
    internal void CompleteEmailConfirmation()
    {
        if (Status == AccountStatus.PendingEmail)
        {
            Status = AccountStatus.PendingEligibility;
        }
    }
}
