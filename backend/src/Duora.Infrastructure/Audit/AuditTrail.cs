using System.Diagnostics;
using Duora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Duora.Infrastructure.Audit;

/// <summary>
/// Grava fatos em `ops.audit_events` dentro da transação do efeito que eles registram (TM-05): se o efeito volta
/// atrás, o registro some junto, e não existe efeito sem registro. A tabela é append-only por GRANT (D3).
/// </summary>
internal sealed class AuditTrail(DuoraDbContext db, TimeProvider time)
{
    /// <exception cref="InvalidOperationException">Sem transação aberta no contexto: o registro ficaria solto do efeito.</exception>
    public Task AppendAsync(AuditRecord record, CancellationToken cancellationToken) =>
        AppendIfAsync(record, effectHappened: true, cancellationToken);

    /// <summary>
    /// Roda o mesmo comando aconteça o efeito ou não; a linha só entra quando <paramref name="effectHappened"/>.
    /// Serve a quem precisa custar o mesmo nos dois caminhos para não abrir canal de tempo (bloqueio, TM-24).
    /// </summary>
    /// <exception cref="InvalidOperationException">Sem transação aberta no contexto: o registro ficaria solto do efeito.</exception>
    public async Task AppendIfAsync(AuditRecord record, bool effectHappened, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Auditoria só é gravada na transação do efeito auditado.");
        }

        var occurredAt = time.GetUtcNow();
        var actorKind = record.ActorKind.ToString();
        var action = record.Action.Code;
        var correlationId = Activity.Current?.TraceId.ToHexString();
        await db.Database.ExecuteSqlAsync(
            $"""
            insert into ops.audit_events
                (occurred_at, actor_kind, actor_id, action, target_type, target_id, target_version, correlation_id)
            select {occurredAt}, {actorKind}, {record.ActorId}, {action}, {record.TargetType}, {record.TargetId},
                   {record.TargetVersion}, {correlationId}
            where {effectHappened}
            """,
            cancellationToken);
    }
}
