using Duora.Core.Safety;
using Duora.Infrastructure.Audit;
using Duora.Infrastructure.Identity;
using Duora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

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
}

/// <summary>
/// `BlockUser` e `CheckInteraction` do Safety (modules.md; spec 7.3; concurrency.md C2). O bloqueio é idempotente
/// pela constraint do par e serializado pelas guardas das duas pessoas, sem check-then-insert.
/// </summary>
public sealed class BlockService
{
    /// <summary>Hipótese de concurrency.md (princípio 5): transação coordenada não espera lock indefinidamente.</summary>
    private const string LockTimeout = "2s";

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
    /// No M2, cada módulo acrescenta aqui, dentro da mesma transação, o próprio efeito (conexão, conversa, sala,
    /// reserva, outbox do par), na ordem global de locks (concurrency.md C2).
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

        if (targetUserId == Guid.Empty)
        {
            return BlockOutcome.Done;
        }

        var requested = Block.Create(blockerId, targetUserId, _time);
        if (requested.Rejection == SafetyRejection.SelfBlock)
        {
            return BlockOutcome.SelfBlock;
        }

        if (await _accounts.FindAccountAsync(targetUserId, cancellationToken) is null)
        {
            return BlockOutcome.Done;
        }

        await RecordAsync(requested.Value, cancellationToken);
        return BlockOutcome.Done;
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

    private async Task RecordAsync(Block block, CancellationToken cancellationToken)
    {
        Guid[] pair = [.. new[] { block.Pair.BlockerId, block.Pair.BlockedId }.Order()];
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        await _db.Database.ExecuteSqlRawAsync($"set local lock_timeout = '{LockTimeout}'", cancellationToken);

        // Guardas sob demanda (G7), na ordem do array, para dois bloqueios cruzados não se travarem na criação.
        await _db.Database.ExecuteSqlAsync(
            $"""
            insert into safety.user_guards (user_id)
            select user_id from unnest({pair}::uuid[]) with ordinality as ids(user_id, position)
            order by position
            on conflict (user_id) do nothing
            """,
            cancellationToken);
        await _db.Database.ExecuteSqlAsync(
            $"select 1 from safety.user_guards where user_id = any({pair}) order by user_id for update",
            cancellationToken);

        var inserted = await _db.Database.ExecuteSqlAsync(
            $"""
            insert into safety.blocks (id, blocker_id, blocked_id, created_at)
            values ({Guid.CreateVersion7(block.CreatedAt)}, {block.Pair.BlockerId}, {block.Pair.BlockedId},
                    {block.CreatedAt})
            on conflict (blocker_id, blocked_id) do nothing
            """,
            cancellationToken);
        if (inserted == 1)
        {
            await _db.Database.ExecuteSqlAsync(
                $"update safety.user_guards set security_version = security_version + 1 where user_id = any({pair})",
                cancellationToken);
            await _audit.AppendAsync(
                new AuditRecord(
                    AuditActorKind.User,
                    block.Pair.BlockerId,
                    AuditAction.SafetyBlockCreated,
                    AuditTargetTypes.User,
                    block.Pair.BlockedId),
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }
}
