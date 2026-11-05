using System.Diagnostics;
using Duora.Infrastructure.Audit;
using Npgsql;
using static Duora.Infrastructure.Participation.ParticipationSql;

namespace Duora.Infrastructure.Operations;

/// <summary>Auditoria da reconciliação na conexão e na transação de quem chama (ADR-0049, Auditoria).</summary>
internal static class ReconciliationAudit
{
    /// <summary>O TraceId do pedido; fora de um pedido rastreado, um id novo, porque o recibo sempre exige correlação.</summary>
    public static string Correlation(DateTimeOffset now) =>
        Activity.Current?.TraceId.ToHexString() ?? Guid.CreateVersion7(now).ToString("N");

    public static Task<int> AppendAsync(NpgsqlConnection connection, AuditRecord record, DateTimeOffset now, string correlation,
        CancellationToken ct) =>
        Execute(connection, """
            insert into ops.audit_events(occurred_at,actor_kind,actor_id,action,target_type,target_id,target_version,correlation_id)
            values($1,$2,$3,$4,$5,$6,$7,nullif($8,''))
            """, ct, now, record.ActorKind.ToString(), record.ActorId is { } actor ? actor : DBNull.Value, record.Action.Code, record.TargetType, record.TargetId,
            record.TargetVersion is { } version ? version : DBNull.Value, correlation);
}
