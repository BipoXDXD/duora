using Duora.Core.Identity;
using Duora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Duora.Infrastructure.Identity;

/// <summary>
/// Reprovações da selfie da conta (issue #300), em <c>identity.selfie_failures</c>. Quem chama já detém a trava da
/// linha da conta, então a leitura depois de gravar enxerga todas as reprovações dela; a chave primária pela
/// tentativa torna a gravação idempotente mesmo sem a trava.
/// </summary>
internal static class SelfieFailureLedger
{
    /// <returns>Se a reprovação é nova; falso quando a mesma tentativa já estava gravada.</returns>
    public static async Task<bool> RecordAsync(DuoraDbContext db, Guid verificationId, Guid userId, DateTimeOffset failedAt,
        CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlAsync($"""
            insert into identity.selfie_failures (verification_id, user_id, failed_at)
            values ({verificationId}, {userId}, {failedAt})
            on conflict (verification_id) do nothing
            """, cancellationToken) == 1;

    public static async Task<IReadOnlyList<DateTimeOffset>> ReadAsync(DuoraDbContext db, Guid userId,
        CancellationToken cancellationToken) =>
        await db.Database.SqlQuery<DateTimeOffset>($"""
            select failed_at as "Value" from identity.selfie_failures where user_id = {userId} order by failed_at
            """).ToListAsync(cancellationToken);

    public static async Task<SelfieAttemptStanding> StandingAsync(DuoraDbContext db, Guid userId, DateTimeOffset now,
        CancellationToken cancellationToken) =>
        SelfieAttemptPolicy.Evaluate([.. await ReadAsync(db, userId, cancellationToken)], now);

    /// <summary>Encerra a contagem: na aprovação e quando o moderador revoga a suspensão por selfie.</summary>
    public static Task ClearAsync(DuoraDbContext db, Guid userId, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlAsync($"delete from identity.selfie_failures where user_id = {userId}", cancellationToken);
}
