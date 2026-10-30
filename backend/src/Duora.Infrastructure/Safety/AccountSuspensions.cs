using System.Data;
using System.Security.Cryptography;
using System.Text;
using Duora.Core.Identity;
using Duora.Core.Safety;
using Duora.Infrastructure.Audit;
using Duora.Infrastructure.Identity;
using Duora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Duora.Infrastructure.Safety;

/// <summary>Sessão administrativa de quem modera, vinda da fronteira autenticada; não é credencial.</summary>
public readonly record struct ModeratorSession(Guid UserId, Guid SessionId);

/// <summary>Uma suspensão como a moderação a vê: sem o motivo interno, que fica só no banco para auditoria.</summary>
public sealed record SuspensionView(
    Guid Id,
    Guid SubjectUserId,
    SuspensionReason Reason,
    bool DecidedAutomatically,
    DateTimeOffset SuspendedAt,
    long Version,
    DateTimeOffset? LiftedAt);

/// <summary>O que a pessoa suspensa vê da própria medida (GET /me): o código do motivo e o instante.</summary>
public sealed record SuspensionNotice(SuspensionReason Reason, DateTimeOffset SuspendedAt);

public abstract record SuspensionOutcome
{
    private SuspensionOutcome()
    {
    }

    /// <param name="View">A medida gravada.</param>
    /// <param name="Replayed">A mesma chave e o mesmo pedido já tinham gravado esta medida; nada mudou agora.</param>
    public sealed record Suspended(SuspensionView View, bool Replayed) : SuspensionOutcome;

    /// <summary>A conta já tem uma suspensão ativa; uma segunda não é criada.</summary>
    public sealed record AlreadySuspended : SuspensionOutcome;

    /// <summary>A chave já gravou outro pedido.</summary>
    public sealed record KeyReused : SuspensionOutcome;

    /// <summary>Conta inexistente ou privilegiada.</summary>
    public sealed record NotFound : SuspensionOutcome;

    /// <summary>Conta em cadastro pendente ou em exclusão.</summary>
    public sealed record NotSuspendable : SuspensionOutcome;

    /// <summary>Quem pediu não é moderador com sessão administrativa válida e TOTP recente.</summary>
    public sealed record Forbidden : SuspensionOutcome;

    /// <summary>Contenção de lock; nada foi gravado e o pedido pode ser repetido com a mesma chave.</summary>
    public sealed record Busy : SuspensionOutcome;
}

public abstract record LiftOutcome
{
    private LiftOutcome()
    {
    }

    public sealed record Lifted(SuspensionView View) : LiftOutcome;

    /// <summary>A medida não existe ou é de outra conta.</summary>
    public sealed record NotFound : LiftOutcome;

    /// <summary>A versão esperada não é a atual: a medida já foi revogada ou mudou.</summary>
    public sealed record PreconditionFailed : LiftOutcome;

    public sealed record Forbidden : LiftOutcome;

    public sealed record Busy : LiftOutcome;
}

public abstract record SuspensionReadOutcome
{
    private SuspensionReadOutcome()
    {
    }

    /// <param name="Active">A suspensão ativa da conta; nula se a conta não está suspensa.</param>
    public sealed record Found(SuspensionView? Active) : SuspensionReadOutcome;

    public sealed record NotFound : SuspensionReadOutcome;

    public sealed record Forbidden : SuspensionReadOutcome;
}

/// <summary>
/// Suspensão de conta (issue #299, ADR-0042). A medida fica em <c>safety.moderation_actions</c> e o estado da conta
/// muda para <c>Suspended</c> na mesma transação, pela operação do Identity. Como a sessão lê o estado da conta a cada
/// requisição, a suspensão vale já na requisição seguinte de toda sessão aberta, e a revogação devolve o acesso do
/// mesmo jeito. Salas convidadas ou ativas da pessoa são abortadas e as reservas liberadas, como no bloqueio.
/// </summary>
/// <remarks>
/// Duas portas de entrada: a rota administrativa (<see cref="SuspendAsModeratorAsync"/>) e a interna, para o
/// sistema (<see cref="SuspendAutomaticallyAsync"/>, a que a issue #300 chama). Cada chamada abre a própria
/// transação READ COMMITTED; quem chama não pode ter uma aberta. Ordem de locks (spec 10.4): guardas de usuário em
/// UUID ordenado, linha da conta, coordenadores de evento e rodada, sessões de jogo.
/// </remarks>
public sealed class AccountSuspensions
{
    private const string LockTimeout = "2s";
    private const string Suspension = "Suspension";

    private readonly DuoraDbContext _db;
    private readonly TimeProvider _time;

    internal AccountSuspensions(DuoraDbContext db, TimeProvider time)
    {
        _db = db;
        _time = time;
    }

    /// <summary>
    /// API interna para o sistema suspender uma conta (issue #300). Idempotente pela chave: repetir com a mesma chave
    /// e o mesmo pedido devolve a mesma medida; se a conta já está suspensa por outra medida, devolve
    /// <see cref="SuspensionOutcome.AlreadySuspended"/> sem criar outra.
    /// </summary>
    /// <param name="order">Pedido de <see cref="SuspensionOrder.Automated"/>.</param>
    /// <param name="key">Chave de <see cref="ModerationCommandKey.ForSystem"/>, ligada ao registro que motivou a suspensão.</param>
    /// <param name="cancellationToken">Cancelamento.</param>
    /// <exception cref="ArgumentException">O pedido não é automático.</exception>
    public Task<SuspensionOutcome> SuspendAutomaticallyAsync(
        SuspensionOrder order,
        ModerationCommandKey key,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order);
        if (order.Author is not ModerationAuthor.Automated)
        {
            throw new ArgumentException("A porta interna só aceita suspensão do sistema.", nameof(order));
        }

        if (key.Source is null)
        {
            throw new ArgumentException("A suspensão do sistema usa a chave da origem que a motivou.", nameof(key));
        }

        return SuspendAsync(moderator: null, order, key, cancellationToken);
    }

    /// <exception cref="ArgumentException">O pedido não foi feito em nome do moderador da sessão.</exception>
    public Task<SuspensionOutcome> SuspendAsModeratorAsync(
        ModeratorSession moderator,
        SuspensionOrder order,
        ModerationCommandKey key,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order);
        if (!order.Author.Is(moderator.UserId))
        {
            throw new ArgumentException("O autor do pedido é o moderador da sessão.", nameof(order));
        }

        if (key.Source is not null)
        {
            throw new ArgumentException("O comando do moderador usa a chave do moderador.", nameof(key));
        }

        return SuspendAsync(moderator, order, key, cancellationToken);
    }

    /// <exception cref="ArgumentException">A revogação não foi feita em nome do moderador da sessão.</exception>
    public async Task<LiftOutcome> LiftAsModeratorAsync(
        ModeratorSession moderator,
        Guid subjectUserId,
        Guid suspensionId,
        long expectedVersion,
        SuspensionLift lift,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lift);
        if (!lift.Author.Is(moderator.UserId))
        {
            throw new ArgumentException("O autor da revogação é o moderador da sessão.", nameof(lift));
        }

        try
        {
            return await LiftAsync(moderator, subjectUserId, suspensionId, expectedVersion, lift, cancellationToken);
        }
        catch (PostgresException error) when (IsContention(error))
        {
            return new LiftOutcome.Busy();
        }
    }

    /// <summary>
    /// Pré-checagem da permissão, sem lock e sem tocar o alvo: a fronteira responde 403 antes de validar corpo ou
    /// chave. Os comandos conferem de novo, ao vivo, na própria transação.
    /// </summary>
    public Task<bool> IsModeratorSessionAsync(ModeratorSession moderator, CancellationToken cancellationToken) =>
        IsModeratorAsync(moderator, requireRecentTotp: false, cancellationToken);

    public async Task<SuspensionReadOutcome> ReadAsModeratorAsync(
        ModeratorSession moderator,
        Guid subjectUserId,
        CancellationToken cancellationToken)
    {
        if (!await IsModeratorAsync(moderator, requireRecentTotp: false, cancellationToken))
        {
            return new SuspensionReadOutcome.Forbidden();
        }

        var subjects = await _db.Database.SqlQuery<bool>($"""
            select exists (select 1 from identity.users u where u.id = {subjectUserId}
              and not exists (select 1 from identity.user_roles ur join identity.roles r on r.id = ur.role_id
                              where ur.user_id = u.id and r.name = any({AccountRoles.Privileged.ToArray()}))) as "Value"
            """).SingleAsync(cancellationToken);
        if (!subjects)
        {
            return new SuspensionReadOutcome.NotFound();
        }

        var active = await ActiveRowsOf(subjectUserId).SingleOrDefaultAsync(cancellationToken);
        return new SuspensionReadOutcome.Found(active?.ToView());
    }

    /// <summary>A suspensão ativa da própria conta, para GET /me; nula se a conta não está suspensa.</summary>
    public async Task<SuspensionNotice?> FindOwnNoticeAsync(Guid userId, CancellationToken cancellationToken) =>
        await ActiveRowsOf(userId).SingleOrDefaultAsync(cancellationToken) is { } row
            ? new SuspensionNotice(row.ParseReason(), row.DecidedAt)
            : null;

    private async Task<SuspensionOutcome> SuspendAsync(
        ModeratorSession? moderator,
        SuspensionOrder order,
        ModerationCommandKey key,
        CancellationToken cancellationToken)
    {
        try
        {
            return await RecordSuspensionAsync(moderator, order, key, cancellationToken);
        }
        catch (PostgresException error) when (IsContention(error))
        {
            return new SuspensionOutcome.Busy();
        }
    }

    private async Task<SuspensionOutcome> RecordSuspensionAsync(
        ModeratorSession? moderator,
        SuspensionOrder order,
        ModerationCommandKey key,
        CancellationToken cancellationToken)
    {
        var subject = order.SubjectUserId;
        var commandKey = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key.Scope)));
        var intent = Intent(order);
        await using var transaction = await BeginAsync(cancellationToken);
        // Permissão antes de criar ou travar a guarda do alvo; depois das guardas, de novo, ao vivo.
        if (moderator is { } early && !await IsModeratorAsync(early, requireRecentTotp: true, cancellationToken))
        {
            return new SuspensionOutcome.Forbidden();
        }

        await GuardUsersAsync(moderator is { } actor ? [actor.UserId, subject] : [subject], cancellationToken);
        if (moderator is { } session && !await IsModeratorAsync(session, requireRecentTotp: true, cancellationToken))
        {
            return new SuspensionOutcome.Forbidden();
        }

        if (await ReplayAsync(commandKey, intent, cancellationToken) is { } replay)
        {
            return replay;
        }

        switch (await AccountSuspensionStatus.LockAsync(_db, subject, cancellationToken))
        {
            case SuspensionStanding.NotFound:
                return new SuspensionOutcome.NotFound();
            case SuspensionStanding.NotSuspendable:
                return new SuspensionOutcome.NotSuspendable();
            case SuspensionStanding.Suspended:
                return new SuspensionOutcome.AlreadySuspended();
            case SuspensionStanding.Suspendable:
                break;
            default:
                throw new InvalidOperationException("Situação da conta desconhecida.");
        }

        var now = _time.GetUtcNow();
        var id = Guid.CreateVersion7(now);
        var reason = order.Reason.ToString();
        var authorKind = AuthorKind(order.Author);
        Guid? authorId = order.Author is ModerationAuthor.Human human ? human.UserId : null;
        // Sem alvo de ON CONFLICT: a chave e a suspensão ativa (índice parcial) recusam sem abortar a transação.
        var inserted = await _db.Database.ExecuteSqlAsync($"""
            insert into safety.moderation_actions
              (id, subject_user_id, measure, reason_code, internal_reason, author_kind, author_user_id, decided_at,
               command_key, command_intent, source_code, source_id, version)
            values ({id}, {subject}, {Suspension}, {reason}, {order.InternalReason}, {authorKind}, {authorId}, {now},
                    {commandKey}, {intent}, {key.Source?.Code}, {key.Source?.Id}, 1)
            on conflict do nothing
            """, cancellationToken);
        if (inserted != 1)
        {
            return await ReplayAsync(commandKey, intent, cancellationToken) ?? new SuspensionOutcome.AlreadySuspended();
        }

        await AccountSuspensionStatus.SuspendAsync(_db, subject, cancellationToken);
        await ReleaseRoomsAsync(subject, now, cancellationToken);
        await BumpSecurityVersionAsync(subject, cancellationToken);
        await AuditAsync(moderator, AuditAction.AccountSuspended, id, version: 1, cancellationToken);
        if (key.Source is { } source)
        {
            await new AuditTrail(_db, _time).AppendAsync(
                new AuditRecord(AuditActorKind.System, actorId: null, AuditAction.AccountSuspensionSourceRecorded,
                    source.Code.Replace('-', '_'), source.Id),
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new SuspensionOutcome.Suspended(
            new SuspensionView(id, subject, order.Reason, order.Author is ModerationAuthor.Automated, now, 1, null),
            Replayed: false);
    }

    private async Task<LiftOutcome> LiftAsync(
        ModeratorSession moderator,
        Guid subjectUserId,
        Guid suspensionId,
        long expectedVersion,
        SuspensionLift lift,
        CancellationToken cancellationToken)
    {
        await using var transaction = await BeginAsync(cancellationToken);
        if (!await IsModeratorAsync(moderator, requireRecentTotp: true, cancellationToken))
        {
            return new LiftOutcome.Forbidden();
        }

        await GuardUsersAsync([moderator.UserId, subjectUserId], cancellationToken);
        if (!await IsModeratorAsync(moderator, requireRecentTotp: true, cancellationToken))
        {
            return new LiftOutcome.Forbidden();
        }

        if (await AccountSuspensionStatus.LockAsync(_db, subjectUserId, cancellationToken) == SuspensionStanding.NotFound)
        {
            return new LiftOutcome.NotFound();
        }

        var now = _time.GetUtcNow();
        var liftedByKind = AuthorKind(lift.Author);
        var lifted = await _db.Database.SqlQuery<ActionRow>($"""
            update safety.moderation_actions
            set version = 2, lifted_at = {now}, lifted_by_kind = {liftedByKind}, lifted_by_user_id = {moderator.UserId},
                lift_reason = {lift.InternalReason}
            where id = {suspensionId} and subject_user_id = {subjectUserId} and measure = {Suspension}
              and version = {expectedVersion} and lifted_at is null
            returning id as "Id", subject_user_id as "SubjectUserId", reason_code as "ReasonCode", author_kind as "AuthorKind",
              decided_at as "DecidedAt", version as "Version", lifted_at as "LiftedAt"
            """).ToListAsync(cancellationToken);
        if (lifted is not [var row])
        {
            var exists = await _db.Database.SqlQuery<bool>($"""
                select exists (select 1 from safety.moderation_actions
                  where id = {suspensionId} and subject_user_id = {subjectUserId} and measure = {Suspension}) as "Value"
                """).SingleAsync(cancellationToken);
            return exists ? new LiftOutcome.PreconditionFailed() : new LiftOutcome.NotFound();
        }

        await AccountSuspensionStatus.RestoreAsync(_db, subjectUserId, cancellationToken);
        if (row.ParseReason() == SuspensionReason.SelfieVerificationFailed)
        {
            // Revogar a suspensão por selfie é a revisão humana prevista na issue #300: a conta volta ao primeiro ciclo.
            await SelfieFailureLedger.ClearAsync(_db, subjectUserId, cancellationToken);
        }

        await BumpSecurityVersionAsync(subjectUserId, cancellationToken);
        await AuditAsync(moderator, AuditAction.AccountSuspensionLifted, suspensionId, version: 2, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new LiftOutcome.Lifted(row.ToView());
    }

    private async Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction> BeginAsync(CancellationToken cancellationToken)
    {
        if (_db.Database.CurrentTransaction is not null || System.Transactions.Transaction.Current is not null)
        {
            throw new InvalidOperationException("A suspensão abre a própria transação; quem chama não pode ter uma aberta.");
        }

        var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await _db.Database.ExecuteSqlRawAsync($"set local lock_timeout = '{LockTimeout}'", cancellationToken);
        return transaction;
    }

    /// <summary>Guardas sob demanda (G7), criadas e travadas em UUID ordenado; id inexistente não ganha guarda.</summary>
    private async Task GuardUsersAsync(Guid[] users, CancellationToken cancellationToken)
    {
        Guid[] ordered = [.. users.Distinct().Order()];
        await _db.Database.ExecuteSqlAsync($"""
            insert into safety.user_guards (user_id)
            select u.id from identity.users u join unnest({ordered}::uuid[]) with ordinality as ids(user_id, position)
              on u.id = ids.user_id
            order by ids.position
            on conflict (user_id) do nothing
            """, cancellationToken);
        await _db.Database.ExecuteSqlAsync(
            $"select 1 from safety.user_guards where user_id = any({ordered}) order by user_id for update",
            cancellationToken);
    }

    /// <summary>
    /// Autorização viva (ADR-0042): papel <c>moderator</c>, conta ativa com MFA, sessão administrativa válida com o
    /// segundo fator provado e, nos comandos, TOTP dentro de <see cref="RecentAuthentication.Window"/>. Papel genérico
    /// de administração ou operação não basta.
    /// </summary>
    private async Task<bool> IsModeratorAsync(ModeratorSession moderator, bool requireRecentTotp, CancellationToken cancellationToken)
    {
        if (moderator.UserId == Guid.Empty || moderator.SessionId == Guid.Empty)
        {
            return false;
        }

        var now = _time.GetUtcNow();
        var proofs = await _db.Database.SqlQuery<SessionProof>($"""
            select s.totp_verified_at as "TotpVerifiedAt"
            from identity.users u join identity.login_sessions s on s.user_id = u.id
            where u.id = {moderator.UserId} and s.id = {moderator.SessionId}
              and u.status = 'Active' and u.email_confirmed and u.two_factor_enabled
              and u.temporary_password_expires_at is null
              and exists (select 1 from identity.user_roles ur join identity.roles r on r.id = ur.role_id
                          where ur.user_id = u.id and r.name = {AccountRoles.Moderator})
              and s.policy = 'Administrative' and s.security_stamp = u.security_stamp
              and s.revoked_at is null and s.idle_expires_at > {now} and s.absolute_expires_at > {now}
              and s.mfa_verified_at is not null and s.mfa_verified_at <= {now}
            """).ToListAsync(cancellationToken);
        return proofs is [var proof] && (!requireRecentTotp || RecentAuthentication.IsRecent(proof.TotpVerifiedAt, now));
    }

    private async Task<SuspensionOutcome?> ReplayAsync(string commandKey, byte[] intent, CancellationToken cancellationToken)
    {
        var previous = await _db.Database.SqlQuery<CommandRow>($"""
            select id as "Id", command_intent as "Intent" from safety.moderation_actions where command_key = {commandKey}
            """).ToListAsync(cancellationToken);
        if (previous is not [var command])
        {
            return null;
        }

        if (!CryptographicOperations.FixedTimeEquals(command.Intent, intent))
        {
            return new SuspensionOutcome.KeyReused();
        }

        var row = await _db.Database.SqlQuery<ActionRow>($"""
            select id as "Id", subject_user_id as "SubjectUserId", reason_code as "ReasonCode", author_kind as "AuthorKind",
              decided_at as "DecidedAt", version as "Version", lifted_at as "LiftedAt"
            from safety.moderation_actions where id = {command.Id}
            """).SingleAsync(cancellationToken);
        return new SuspensionOutcome.Suspended(row.ToView(), Replayed: true);
    }

    /// <summary>Mesmo protocolo do bloqueio (spec 7.3, 10.4): convites e salas ativas abortadas, reservas liberadas.</summary>
    private async Task ReleaseRoomsAsync(Guid subject, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await _db.Database.ExecuteSqlAsync($"""
            select 1 from events.events where id in
              (select event_id from events.pairings
               where (user_low = {subject} or user_high = {subject}) and released_at is null)
            order by id for update
            """, cancellationToken);
        await _db.Database.ExecuteSqlAsync($"""
            select 1 from events.rounds where id in
              (select round_id from events.pairings
               where (user_low = {subject} or user_high = {subject}) and released_at is null)
            order by id for update
            """, cancellationToken);
        await _db.Database.ExecuteSqlAsync($"""
            select 1 from experiences.game_sessions where pairing_id in
              (select id from events.pairings
               where (user_low = {subject} or user_high = {subject}) and released_at is null)
            order by id for update
            """, cancellationToken);
        await _db.Database.ExecuteSqlAsync($"""
            update experiences.game_sessions set state = 'Aborted', version = version + 1
            where state in ('Invited', 'Active') and pairing_id in
              (select id from events.pairings where user_low = {subject} or user_high = {subject})
            """, cancellationToken);
        await _db.Database.ExecuteSqlAsync($"""
            update events.pairing_participants set released_at = {now}
            where released_at is null and pairing_id in
              (select id from events.pairings where user_low = {subject} or user_high = {subject})
            """, cancellationToken);
        await _db.Database.ExecuteSqlAsync($"""
            update events.pairings set released_at = {now}, release_reason = 'Unavailable'
            where released_at is null and (user_low = {subject} or user_high = {subject})
            """, cancellationToken);
    }

    private async Task BumpSecurityVersionAsync(Guid subject, CancellationToken cancellationToken) =>
        await _db.Database.ExecuteSqlAsync(
            $"update safety.user_guards set security_version = security_version + 1 where user_id = {subject}",
            cancellationToken);

    private Task AuditAsync(
        ModeratorSession? moderator,
        AuditAction action,
        Guid moderationActionId,
        long version,
        CancellationToken cancellationToken) =>
        new AuditTrail(_db, _time).AppendAsync(
            moderator is { } actor
                ? new AuditRecord(AuditActorKind.Moderator, actor.UserId, action, AuditTargetTypes.ModerationAction,
                    moderationActionId, version)
                : new AuditRecord(AuditActorKind.System, actorId: null, action, AuditTargetTypes.ModerationAction,
                    moderationActionId, version),
            cancellationToken);

    private IQueryable<ActionRow> ActiveRowsOf(Guid userId) =>
        _db.Database.SqlQuery<ActionRow>($"""
            select id as "Id", subject_user_id as "SubjectUserId", reason_code as "ReasonCode", author_kind as "AuthorKind",
              decided_at as "DecidedAt", version as "Version", lifted_at as "LiftedAt"
            from safety.moderation_actions
            where subject_user_id = {userId} and measure = {Suspension} and lifted_at is null
            """);

    /// <summary>Hash do pedido (conta, medida, motivo e texto), para a mesma chave com outro pedido virar 409.</summary>
    private static byte[] Intent(SuspensionOrder order) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{order.SubjectUserId:D}\n{Suspension}\n{order.Reason}\n{AuthorKind(order.Author)}\n{order.InternalReason}"));

    private static string AuthorKind(ModerationAuthor author) =>
        author is ModerationAuthor.Human ? "Human" : "Automated";

    private static bool IsContention(PostgresException error) =>
        error.SqlState is PostgresErrorCodes.LockNotAvailable or PostgresErrorCodes.DeadlockDetected;

    private sealed record SessionProof(DateTimeOffset? TotpVerifiedAt);

    private sealed record CommandRow(Guid Id, byte[] Intent);

    private sealed record ActionRow(
        Guid Id,
        Guid SubjectUserId,
        string ReasonCode,
        string AuthorKind,
        DateTimeOffset DecidedAt,
        long Version,
        DateTimeOffset? LiftedAt)
    {
        public SuspensionReason ParseReason() => Enum.Parse<SuspensionReason>(ReasonCode);

        public SuspensionView ToView() =>
            new(Id, SubjectUserId, ParseReason(), AuthorKind == "Automated", DecidedAt, Version, LiftedAt);
    }
}
