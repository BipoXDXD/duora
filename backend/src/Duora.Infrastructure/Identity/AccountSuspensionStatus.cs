using Duora.Core.Identity;
using Duora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Duora.Infrastructure.Identity;

/// <summary>Como a conta está para quem vai suspendê-la ou revogar a suspensão, lida sob lock da linha.</summary>
internal enum SuspensionStanding
{
    /// <summary>Inexistente ou privilegiada: moderação de conta só alcança participantes, e a resposta é a mesma.</summary>
    NotFound,

    /// <summary>Participante em estado que a suspensão não cobre (cadastro pendente ou exclusão em curso).</summary>
    NotSuspendable,

    Suspendable,

    Suspended,
}

/// <summary>
/// Transição de estado da conta para a suspensão (spec 4.2). Identity é dono de <c>identity.users</c>: o Safety decide
/// a medida e chama estas operações dentro da própria transação, depois das guardas de usuário (spec 10.4). O estado
/// anterior fica em <c>status_before_suspension</c>, e revogar a suspensão o devolve; nenhuma transição acontece por
/// decurso de prazo.
/// </summary>
internal static class AccountSuspensionStatus
{
    /// <summary>Estados de onde a suspensão parte; o mesmo conjunto do CHECK de <c>status_before_suspension</c>.</summary>
    private static readonly string[] SuspendableStatuses =
        [nameof(AccountStatus.PendingEligibility), nameof(AccountStatus.Active), nameof(AccountStatus.Restricted)];

    /// <summary>Trava a linha da conta (FOR UPDATE) e diz o que a moderação pode fazer com ela.</summary>
    public static async Task<SuspensionStanding> LockAsync(DuoraDbContext db, Guid userId, CancellationToken cancellationToken)
    {
        var rows = await db.Database.SqlQuery<AccountRow>($"""
            select u.status as "Status",
                   exists (select 1 from identity.user_roles ur join identity.roles r on r.id = ur.role_id
                           where ur.user_id = u.id and r.name = any({AccountRoles.Privileged.ToArray()})) as "Privileged"
            from identity.users u where u.id = {userId}
            for update of u
            """).ToListAsync(cancellationToken);
        if (rows is not [var row] || row.Privileged)
        {
            return SuspensionStanding.NotFound;
        }

        if (row.Status == nameof(AccountStatus.Suspended))
        {
            return SuspensionStanding.Suspended;
        }

        return SuspendableStatuses.Contains(row.Status, StringComparer.Ordinal)
            ? SuspensionStanding.Suspendable
            : SuspensionStanding.NotSuspendable;
    }

    /// <exception cref="InvalidOperationException">A conta mudou sob a própria trava: defeito de quem chama.</exception>
    public static async Task SuspendAsync(DuoraDbContext db, Guid userId, CancellationToken cancellationToken)
    {
        var updated = await db.Database.ExecuteSqlAsync($"""
            update identity.users
            set status_before_suspension = status, status = {nameof(AccountStatus.Suspended)},
                concurrency_stamp = {Guid.NewGuid().ToString("D")}
            where id = {userId} and status = any({SuspendableStatuses})
            """, cancellationToken);
        if (updated != 1)
        {
            throw new InvalidOperationException("Conta mudou sob a própria trava.");
        }
    }

    /// <summary>
    /// Devolve o estado anterior à suspensão. Se a conta saiu de <c>Suspended</c> por outro caminho (exclusão, por
    /// exemplo), nada muda: revogar a medida nunca reativa uma conta que já seguiu outro fluxo.
    /// </summary>
    /// <returns>Se o estado anterior foi devolvido.</returns>
    public static async Task<bool> RestoreAsync(DuoraDbContext db, Guid userId, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlAsync($"""
            update identity.users
            set status = status_before_suspension, status_before_suspension = null,
                concurrency_stamp = {Guid.NewGuid().ToString("D")}
            where id = {userId} and status = {nameof(AccountStatus.Suspended)} and status_before_suspension is not null
            """, cancellationToken) == 1;

    private sealed record AccountRow(string Status, bool Privileged);
}
