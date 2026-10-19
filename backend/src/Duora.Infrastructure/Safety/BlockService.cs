using Duora.Core.Jobs;
using Duora.Core.Safety;
using Duora.Infrastructure.Audit;
using Duora.Infrastructure.Identity;
using Duora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Duora.Infrastructure.Safety;

/// <summary>Resultado de um pedido de bloqueio, para a fronteira traduzir em HTTP.</summary>
public enum BlockOutcome
{
    /// <summary>Bloqueado, já bloqueado ou alvo inexistente: as três respostas são iguais (G3, TM-24).</summary>
    Done,
    SelfBlock,
    AccountNotEligible,

    /// <summary>A sessão aponta para uma conta que não existe mais.</summary>
    UnknownAccount,

    /// <summary>As guardas continuaram trancadas depois de todas as tentativas; nada foi gravado.</summary>
    Busy,
}

/// <summary>
/// `BlockUser` e `CheckInteraction` do Safety (modules.md; spec 7.3; concurrency.md C2). O bloqueio é idempotente
/// pela constraint do par e serializado pelas guardas das duas pessoas, sem check-then-insert.
/// </summary>
public sealed class BlockService
{
    /// <summary>Hipótese de concurrency.md (princípio 5): transação coordenada não espera lock indefinidamente.</summary>
    private const string LockTimeout = "2s";

    /// <summary>Tentativas da transação inteira quando o lock não vem a tempo ou há deadlock (spec 10.5).</summary>
    private const int MaxAttempts = 3;

    private static readonly ExponentialBackoff ContentionBackoff =
        new(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(400));

    private readonly DuoraDbContext _db;
    private readonly AccountService _accounts;
    private readonly AuditTrail _audit;
    private readonly TimeProvider _time;

    internal BlockService(DuoraDbContext db, AccountService accounts, AuditTrail audit, TimeProvider time)
    {
        _db = db;
        _accounts = accounts;
        _audit = audit;
        _time = time;
    }

    /// <summary>
    /// Bloqueia na hora (spec 12.3). Numa transação: cria as guardas que faltam, tranca as duas em ordem de UUID,
    /// insere o bloqueio com <c>ON CONFLICT DO NOTHING</c> e, se ele é novo, sobe a versão de segurança das duas e
    /// audita. Alvo inexistente não grava nada e responde igual (G3).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Depois da checagem de auto-bloqueio, todo alvo roda os mesmos comandos (TM-24): o alvo inexistente cai no
    /// mesmo caminho de um alvo já bloqueado e de um primeiro bloqueio, sem saída antecipada. Variam as linhas
    /// afetadas e, sobretudo, a espera: alvo inexistente não tem guarda e nunca espera por ela. Esse oráculo residual
    /// está descrito no TM-24, com o teto que o rate limit impõe.
    /// </para>
    /// <para>
    /// No M2, cada módulo acrescenta aqui, dentro da mesma transação, o próprio efeito (conexão, conversa, sala,
    /// reserva, outbox do par), na ordem global de locks (concurrency.md C2).
    /// </para>
    /// </remarks>
    public async Task<BlockOutcome> BlockAsync(Guid blockerId, Guid targetUserId, CancellationToken cancellationToken)
    {
        if (await _accounts.FindAccountAsync(blockerId, cancellationToken) is not { } blocker)
        {
            return BlockOutcome.UnknownAccount;
        }

        if (!BlockEligibility.AllowsBlocking(blocker.Status))
        {
            return BlockOutcome.AccountNotEligible;
        }

        if (targetUserId != Guid.Empty
            && Block.Create(blockerId, targetUserId, _time).Rejection == SafetyRejection.SelfBlock)
        {
            return BlockOutcome.SelfBlock;
        }

        var targetExists = await _accounts.ExistsAsync(targetUserId, cancellationToken);
        return await RecordWithRetryAsync(blockerId, targetUserId, targetExists, cancellationToken);
    }

    /// <summary>
    /// INV-03: duas pessoas podem interagir agora? Bilateral e neutra: não diz quem bloqueou. Leitura sem lock
    /// (concurrency.md C2): uma consulta que começa depois do commit do bloqueio já o vê.
    /// </summary>
    /// <remarks>Sanções entram aqui com a moderação (<see cref="InteractionPolicy"/>).</remarks>
    public async Task<InteractionVerdict> CheckInteractionAsync(
        Guid personA,
        Guid personB,
        CancellationToken cancellationToken)
    {
        if (personA == personB)
        {
            throw new ArgumentException("Interação exige duas pessoas diferentes.", nameof(personB));
        }

        var blocked = await _db.Database
            .SqlQuery<bool>(
                $"""
                select exists (
                    select 1 from safety.blocks
                    where (blocker_id = {personA} and blocked_id = {personB})
                       or (blocker_id = {personB} and blocked_id = {personA})) as "Value"
                """)
            .SingleAsync(cancellationToken);
        return blocked ? InteractionVerdict.Unavailable : InteractionVerdict.Allowed;
    }

    /// <summary>
    /// Repete a transação inteira em <c>lock_timeout</c> (55P03) e deadlock (40P01), com backoff e jitter; esgotadas
    /// as tentativas, devolve <see cref="BlockOutcome.Busy"/> (concurrency.md: 503 com Retry-After). Conta do alvo
    /// apagada entre a checagem e o upsert da guarda (23503) é alvo inexistente: nada foi gravado, mesma resposta.
    /// </summary>
    private async Task<BlockOutcome> RecordWithRetryAsync(
        Guid blockerId,
        Guid targetUserId,
        bool targetExists,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await RecordAsync(blockerId, targetUserId, targetExists, cancellationToken);
                return BlockOutcome.Done;
            }
            catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.ForeignKeyViolation)
            {
                return BlockOutcome.Done;
            }
            catch (PostgresException error) when (IsContention(error))
            {
                if (attempt == MaxAttempts)
                {
                    return BlockOutcome.Busy;
                }

                var delay = ContentionBackoff.DelayAfter(attempt, Random.Shared.NextDouble());
                await Task.Delay(delay, _time, cancellationToken);
            }
        }
    }

    private static bool IsContention(PostgresException error) =>
        error.SqlState is PostgresErrorCodes.LockNotAvailable or PostgresErrorCodes.DeadlockDetected;

    /// <summary>
    /// Com <c>targetExists</c> falso (id inexistente, inclusive o UUID vazio), o alvo não ganha guarda e o insert do
    /// bloqueio exige a guarda do alvo, então nada é gravado. Os comandos são os mesmos.
    /// </summary>
    private async Task RecordAsync(
        Guid blockerId,
        Guid targetUserId,
        bool targetExists,
        CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        Guid[] pair = [.. new[] { blockerId, targetUserId }.Order()];
        Guid[] guarded = targetExists ? pair : [blockerId];
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        await _db.Database.ExecuteSqlRawAsync($"set local lock_timeout = '{LockTimeout}'", cancellationToken);

        // Guardas sob demanda (G7), na ordem do array, para dois bloqueios cruzados não se travarem na criação.
        await _db.Database.ExecuteSqlAsync(
            $"""
            insert into safety.user_guards (user_id)
            select user_id from unnest({guarded}::uuid[]) with ordinality as ids(user_id, position)
            order by position
            on conflict (user_id) do nothing
            """,
            cancellationToken);
        await _db.Database.ExecuteSqlAsync(
            $"select 1 from safety.user_guards where user_id = any({pair}) order by user_id for update",
            cancellationToken);

        // M2: after ordered user guards, lock affected event/round/session rows in global order.
        await _db.Database.ExecuteSqlAsync(
            $"""
            select 1 from events.events where id in
              (select event_id from events.pairings where user_low={pair[0]} and user_high={pair[1]} and released_at is null)
            order by id for update
            """, cancellationToken);
        await _db.Database.ExecuteSqlAsync(
            $"""
            select 1 from events.rounds where id in
              (select round_id from events.pairings where user_low={pair[0]} and user_high={pair[1]} and released_at is null)
            order by id for update
            """, cancellationToken);
        await _db.Database.ExecuteSqlAsync(
            $"""
            select 1 from experiences.game_sessions where pairing_id in
              (select id from events.pairings where user_low={pair[0]} and user_high={pair[1]} and released_at is null)
            order by id for update
            """, cancellationToken);
        await _db.Database.ExecuteSqlAsync(
            $"""
            update experiences.game_sessions set state='Aborted',version=version+1
            where state in ('Invited','Active') and pairing_id in
              (select id from events.pairings where user_low={pair[0]} and user_high={pair[1]})
            """, cancellationToken);
        await _db.Database.ExecuteSqlAsync(
            $"""
            update events.pairing_participants set released_at={now}
            where released_at is null and pairing_id in
              (select id from events.pairings where user_low={pair[0]} and user_high={pair[1]})
            """, cancellationToken);
        await _db.Database.ExecuteSqlAsync(
            $"""
            update events.pairings set released_at={now},release_reason='Unavailable'
            where released_at is null and user_low={pair[0]} and user_high={pair[1]}
            """, cancellationToken);

        var inserted = await _db.Database.ExecuteSqlAsync(
            $"""
            insert into safety.blocks (id, blocker_id, blocked_id, created_at)
            select {Guid.CreateVersion7(now)}, {blockerId}, {targetUserId}, {now}
            where exists (select 1 from safety.user_guards where user_id = {targetUserId})
            on conflict (blocker_id, blocked_id) do nothing
            """,
            cancellationToken);
        // Sempre os mesmos comandos, com efeito só quando o bloqueio é novo: repetir o bloqueio ou mirar um id
        // inexistente roda o que o primeiro bloqueio roda (TM-24).
        var isNew = inserted == 1;
        await _db.Database.ExecuteSqlAsync(
            $"""
            update safety.user_guards set security_version = security_version + 1
            where user_id = any({pair}) and {isNew}
            """,
            cancellationToken);
        await _audit.AppendIfAsync(
            new AuditRecord(
                AuditActorKind.User,
                blockerId,
                AuditAction.SafetyBlockCreated,
                AuditTargetTypes.User,
                // O UUID vazio nunca vira bloqueio (isNew é falso); o registro só precisa ser válido para o comando
                // rodar igual.
                targetUserId == Guid.Empty ? blockerId : targetUserId),
            isNew,
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }
}
