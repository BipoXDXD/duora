using System.Security.Cryptography;
using Duora.Core.Identity;
using Duora.Core.Notifications;
using Duora.Infrastructure.Audit;
using Duora.Infrastructure.Email;
using Duora.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Duora.Infrastructure.Identity;

/// <summary>Resultado de <see cref="AdministratorBootstrap.GrantAsync"/> e <see cref="AdministratorBootstrap.ReissueAsync"/>.</summary>
public abstract record AdministratorCredential
{
    private AdministratorCredential()
    {
    }

    /// <summary>
    /// Senha temporária emitida. <paramref name="TemporaryPassword"/> só existe neste objeto: o banco guarda o hash, e
    /// quem chamou a mostra uma vez a quem rodou o comando.
    /// </summary>
    public sealed record Issued(Guid UserId, string TemporaryPassword, DateTimeOffset ExpiresAt) : AdministratorCredential
    {
        /// <summary>Sem a senha: o texto de um record pode acabar num log ou numa mensagem de erro.</summary>
        public override string ToString() => $"{nameof(Issued)} {{ UserId = {UserId}, ExpiresAt = {ExpiresAt:O} }}";
    }

    /// <summary>Nenhuma conta mudou: o e-mail não serve para a operação pedida.</summary>
    public sealed record Refused : AdministratorCredential;
}

/// <summary>
/// Bootstrap administrativo (spec 11.1; auth.md, D4). <see cref="GrantAsync"/> cria uma conta privilegiada nova com
/// senha temporária forte, que expira em <see cref="TemporaryPasswordLifetime"/> e só serve para ser trocada (a sessão
/// restrita do MFA vem depois). Nunca promove conta existente: contas privilegiada e de participante são disjuntas
/// (D13), e o <c>INSERT ... ON CONFLICT DO NOTHING</c> deixa intacto qualquer cadastro do mesmo e-mail.
/// <see cref="ReissueAsync"/>, pedido à parte (<c>--reissue</c>), é a única mudança numa conta existente: a do mesmo
/// papel, ainda sem MFA. Cada operação grava conta, papel, auditoria e aviso numa transação só.
/// </summary>
public sealed class AdministratorBootstrap
{
    public static readonly TimeSpan TemporaryPasswordLifetime = TimeSpan.FromHours(24);

    private const int SecurityStampBytes = 20;
    private const string ReissueRevocationReason = "temporary_password_reissued";

    private readonly DuoraDbContext _db;
    private readonly UserManager<DuoraUser> _users;
    private readonly AuditTrail _audit;
    private readonly TimeProvider _time;

    internal AdministratorBootstrap(DuoraDbContext db, UserManager<DuoraUser> users, TimeProvider time)
    {
        _db = db;
        _users = users;
        _audit = new AuditTrail(db, time);
        _time = time;
    }

    /// <summary>Cria a conta privilegiada com a senha temporária; e-mail que já tem conta é recusado sem mudança.</summary>
    /// <exception cref="ArgumentException">O papel não está em <see cref="AccountRoles.Privileged"/>.</exception>
    public async Task<AdministratorCredential> GrantAsync(
        EmailAddress email,
        string role,
        OperatorName operatorName,
        CancellationToken cancellationToken)
    {
        var issue = NewIssue(email, role, operatorName);
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        if (await InsertAccountAsync(issue, cancellationToken) is not { } userId)
        {
            return new AdministratorCredential.Refused();
        }

        await GrantRoleAsync(userId, role, cancellationToken);
        await _audit.AppendAsync(
            AuditRecord.ByTerminalOperator(operatorName, AuditAction.AdminGranted(role), AuditTargetTypes.User, userId),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new AdministratorCredential.Issued(userId, issue.Password, issue.ExpiresAt);
    }

    /// <summary>
    /// Senha temporária nova para a conta do mesmo papel que ainda não ativou o MFA: a anterior venceu ou se perdeu, ou
    /// a pessoa trocou a senha e não chegou ao MFA. A senha atual e as sessões deixam de valer, a conta volta à fase da
    /// troca de senha, e o titular recebe o aviso de senha redefinida (TM-21) no mesmo commit. Conta com MFA, de outro
    /// papel, de participante ou inexistente é recusada sem mudança.
    /// </summary>
    /// <exception cref="ArgumentException">O papel não está em <see cref="AccountRoles.Privileged"/>.</exception>
    public async Task<AdministratorCredential> ReissueAsync(
        EmailAddress email,
        string role,
        OperatorName operatorName,
        CancellationToken cancellationToken)
    {
        var issue = NewIssue(email, role, operatorName);
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        if (await ReplaceTemporaryPasswordAsync(issue, cancellationToken) is not { } account)
        {
            return new AdministratorCredential.Refused();
        }

        await RevokeAllSessionsAsync(account.UserId, cancellationToken);
        await _audit.AppendAsync(
            AuditRecord.ByTerminalOperator(
                operatorName,
                AuditAction.AdminTemporaryPasswordReissued,
                AuditTargetTypes.User,
                account.UserId),
            cancellationToken);
        await NotifyHolderAsync(account, issue.Now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new AdministratorCredential.Issued(account.UserId, issue.Password, issue.ExpiresAt);
    }

    private TemporaryIssue NewIssue(EmailAddress email, string role, OperatorName operatorName)
    {
        ArgumentNullException.ThrowIfNull(email);
        ArgumentNullException.ThrowIfNull(operatorName);
        if (!AccountRoles.IsPrivileged(role))
        {
            throw new ArgumentException("O bootstrap só concede papel privilegiado.", nameof(role));
        }

        var now = _time.GetUtcNow();
        var password = TemporaryPassword.Generate();
        // O PasswordHasher padrão não usa o usuário (o cadastro faz o mesmo); a conta nem existe quando o hash é
        // calculado, e no reemitir ele precisa sair antes da transação, que fica curta.
        var passwordHash = _users.PasswordHasher.HashPassword(null!, password);
        return new TemporaryIssue(
            email,
            _users.NormalizeEmail(email.Value),
            role,
            password,
            passwordHash,
            now,
            now + TemporaryPasswordLifetime);
    }

    private sealed record ReissuedAccount(Guid UserId, string Email);

    /// <summary>
    /// Conta já confirmada (o operador responde pelo endereço) e ativa, sem MFA. Devolve null se o e-mail já tem
    /// conta, sem tocar nela; dois comandos simultâneos com o mesmo e-mail se serializam no índice único.
    /// </summary>
    private async Task<Guid?> InsertAccountAsync(TemporaryIssue issue, CancellationToken cancellationToken)
    {
        var userId = Guid.CreateVersion7(issue.Now);
        var securityStamp = NewSecurityStamp();
        var concurrencyStamp = Guid.NewGuid().ToString("D");
        var active = nameof(AccountStatus.Active);
        var rows = await _db.Database.SqlQuery<Guid>(
            $"""
            insert into identity.users
                (id, user_name, normalized_user_name, email, normalized_email, email_confirmed, password_hash,
                 security_stamp, concurrency_stamp, phone_number_confirmed, two_factor_enabled, lockout_enabled,
                 access_failed_count, status, created_at, temporary_password_expires_at)
            values
                ({userId}, {issue.Email.Value}, {issue.NormalizedEmail}, {issue.Email.Value}, {issue.NormalizedEmail}, true,
                 {issue.PasswordHash}, {securityStamp}, {concurrencyStamp}, false, false, true,
                 0, {active}, {issue.Now}, {issue.ExpiresAt})
            on conflict (normalized_email) do nothing
            returning id as "Value"
            """).ToListAsync(cancellationToken);
        return rows.Count == 1 ? rows[0] : null;
    }

    /// <summary>
    /// Um único UPDATE condicional: só casa a conta confirmada, ativa, sem MFA e com o papel pedido, e qualquer outra
    /// fica intacta. Stamp novo derruba as sessões; o bloqueio por tentativas é zerado, para quem errou a senha
    /// anterior não trancar a nova.
    /// </summary>
    private async Task<ReissuedAccount?> ReplaceTemporaryPasswordAsync(TemporaryIssue issue, CancellationToken cancellationToken)
    {
        var active = nameof(AccountStatus.Active);
        var rows = await _db.Database.SqlQuery<ReissuedAccount>(
            $"""
            update identity.users u
            set password_hash = {issue.PasswordHash},
                security_stamp = {NewSecurityStamp()},
                concurrency_stamp = {Guid.NewGuid().ToString("D")},
                temporary_password_expires_at = {issue.ExpiresAt},
                access_failed_count = 0,
                lockout_end = null
            where u.normalized_email = {issue.NormalizedEmail}
              and u.email_confirmed
              and not u.two_factor_enabled
              and u.status = {active}
              and exists (
                  select 1 from identity.user_roles ur join identity.roles r on r.id = ur.role_id
                  where ur.user_id = u.id and r.name = {issue.Role})
            returning u.id as "UserId", u.email as "Email"
            """).ToListAsync(cancellationToken);
        return rows.Count == 1 ? rows[0] : null;
    }

    /// <summary>O stamp novo já invalida as sessões; a revogação explícita deixa o motivo registrado.</summary>
    private async Task RevokeAllSessionsAsync(Guid userId, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        await _db.Database.ExecuteSqlAsync(
            $"""
            update identity.login_sessions set revoked_at = {now}, revocation_reason = {ReissueRevocationReason}
            where user_id = {userId} and revoked_at is null
            """,
            cancellationToken);
    }

    /// <summary>
    /// Aviso de senha redefinida ao endereço gravado (TM-21). O aviso sai nos dois idiomas no mesmo e-mail, e o
    /// comando não tem requisição de onde tirar um; o payload leva o padrão.
    /// </summary>
    private async Task NotifyHolderAsync(ReissuedAccount account, DateTimeOffset now, CancellationToken cancellationToken)
    {
        _db.Add(AccountEmailOutbox.CreateSecurityNotice(
            SecurityChange.PasswordReset,
            account.Email,
            account.UserId,
            EmailLanguageTag.Default,
            now));
        await _db.SaveChangesAsync(cancellationToken);
    }

    private static string NewSecurityStamp() => Convert.ToHexString(RandomNumberGenerator.GetBytes(SecurityStampBytes));

    /// <summary>O trigger D13 do banco continua sendo a garantia de que a conta não mistura papéis.</summary>
    private async Task GrantRoleAsync(Guid userId, string role, CancellationToken cancellationToken)
    {
        var granted = await _db.Database.ExecuteSqlAsync(
            $"""
            insert into identity.user_roles (user_id, role_id)
            select {userId}, r.id from identity.roles r where r.name = {role}
            """,
            cancellationToken);
        if (granted != 1)
        {
            throw new InvalidOperationException("Papel privilegiado ausente do catálogo identity.roles.");
        }
    }
}

/// <summary>Uma emissão em andamento: a senha em claro e o hash dela existem só aqui, até a saída do comando.</summary>
internal sealed record TemporaryIssue(
    EmailAddress Email,
    string NormalizedEmail,
    string Role,
    string Password,
    string PasswordHash,
    DateTimeOffset Now,
    DateTimeOffset ExpiresAt)
{
    /// <summary>Sem senha nem hash: o texto de um record pode acabar num log ou numa mensagem de erro.</summary>
    public override string ToString() => $"{nameof(TemporaryIssue)} {{ Role = {Role}, ExpiresAt = {ExpiresAt:O} }}";
}
