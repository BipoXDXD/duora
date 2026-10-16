using System.Security.Cryptography;
using Duora.Core.Identity;
using Duora.Infrastructure.Audit;
using Duora.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Duora.Infrastructure.Identity;

/// <summary>Resultado de <see cref="AdministratorBootstrap.GrantAsync"/>.</summary>
public abstract record AdministratorGrant
{
    private AdministratorGrant()
    {
    }

    /// <summary>
    /// Senha temporária emitida. <paramref name="TemporaryPassword"/> só existe neste objeto: o banco guarda o hash, e
    /// quem chamou a mostra uma vez a quem rodou o comando. <paramref name="Reissued"/>: a conta já existia, com o
    /// mesmo papel, e nunca trocou a senha temporária (vencida ou perdida); a anterior deixou de valer.
    /// </summary>
    public sealed record Issued(Guid UserId, string TemporaryPassword, DateTimeOffset ExpiresAt, bool Reissued)
        : AdministratorGrant;

    /// <summary>
    /// Já existe conta com o e-mail que não é um bootstrap pendente do mesmo papel (participante, cadastro pendente,
    /// conta privilegiada já ativada ou de outro papel): nada mudou.
    /// </summary>
    public sealed record AccountExists : AdministratorGrant;
}

/// <summary>
/// Bootstrap administrativo (spec 11.1; auth.md, D4): cria uma conta privilegiada nova com senha temporária forte,
/// que expira em <see cref="TemporaryPasswordLifetime"/> e só serve para ser trocada (a sessão restrita do MFA vem
/// depois). Nunca promove conta existente: contas privilegiada e de participante são disjuntas (D13), e o
/// <c>INSERT ... ON CONFLICT DO NOTHING</c> deixa intacto qualquer cadastro do mesmo e-mail. A única conta existente
/// que o comando toca é a do próprio bootstrap, com o mesmo papel, que ainda não trocou a senha temporária: ela
/// recebe uma senha nova (a anterior venceu ou se perdeu), com stamp novo, o que derruba sessões abertas com a
/// anterior. Conta, papel e auditoria entram na mesma transação.
/// </summary>
public sealed class AdministratorBootstrap
{
    public static readonly TimeSpan TemporaryPasswordLifetime = TimeSpan.FromHours(24);

    /// <summary>24 símbolos de um alfabeto de 32: 120 bits, dentro da política de senha (12 a 128).</summary>
    private const int TemporaryPasswordLength = 24;

    /// <summary>Base32 de Crockford: sem I, L, O e U, que se confundem ao copiar do terminal.</summary>
    private const string CrockfordBase32Symbols = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    private const int SecurityStampBytes = 20;

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

    /// <summary>Emite a senha temporária de uma conta privilegiada nova (ou do bootstrap pendente do mesmo papel).</summary>
    /// <exception cref="ArgumentException">O papel não está em <see cref="AccountRoles.Privileged"/>.</exception>
    public async Task<AdministratorGrant> GrantAsync(
        EmailAddress email,
        string role,
        OperatorName operatorName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(email);
        ArgumentNullException.ThrowIfNull(operatorName);
        if (!AccountRoles.IsPrivileged(role))
        {
            throw new ArgumentException("O bootstrap só concede papel privilegiado.", nameof(role));
        }

        var now = _time.GetUtcNow();
        var expiresAt = now + TemporaryPasswordLifetime;
        var password = RandomNumberGenerator.GetString(CrockfordBase32Symbols, TemporaryPasswordLength);

        var passwordHash = _users.PasswordHasher.HashPassword(null!, password);
        var account = new BootstrapAccount(email, _users.NormalizeEmail(email.Value), role, passwordHash, now, expiresAt);

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        Guid userId;
        var reissued = false;
        if (await InsertAccountAsync(account, cancellationToken) is { } created)
        {
            await GrantRoleAsync(created, role, cancellationToken);
            userId = created;
        }
        else if (await ReissueTemporaryPasswordAsync(account, cancellationToken) is { } pending)
        {
            userId = pending;
            reissued = true;
        }
        else
        {
            return new AdministratorGrant.AccountExists();
        }

        await _audit.AppendAsync(
            AuditRecord.ByTerminalOperator(operatorName, AuditAction.AdminGranted, AuditTargetTypes.User, userId),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new AdministratorGrant.Issued(userId, password, expiresAt, reissued);
    }

    private sealed record BootstrapAccount(
        EmailAddress Email,
        string NormalizedEmail,
        string Role,
        string PasswordHash,
        DateTimeOffset Now,
        DateTimeOffset ExpiresAt);

    /// <summary>
    /// Conta já confirmada (o operador responde pelo endereço) e ativa, sem MFA. Devolve null se o e-mail já tem
    /// conta, sem tocar nela.
    /// </summary>
    private async Task<Guid?> InsertAccountAsync(BootstrapAccount account, CancellationToken cancellationToken)
    {
        var (email, normalizedEmail, _, passwordHash, now, expiresAt) = account;
        var userId = Guid.CreateVersion7(now);
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
                ({userId}, {email.Value}, {normalizedEmail}, {email.Value}, {normalizedEmail}, true, {passwordHash},
                 {securityStamp}, {concurrencyStamp}, false, false, true,
                 0, {active}, {now}, {expiresAt})
            on conflict (normalized_email) do nothing
            returning id as "Value"
            """).ToListAsync(cancellationToken);
        return rows.Count == 1 ? rows[0] : null;
    }

    /// <summary>
    /// Senha temporária nova para o bootstrap pendente do mesmo papel, num único UPDATE condicional: conta que já
    /// trocou a senha, que tem outro papel ou que é de participante não casa e fica intacta. O bloqueio por tentativas
    /// é zerado (quem errou a senha anterior não tranca a nova), e o stamp novo encerra as sessões da anterior.
    /// </summary>
    private async Task<Guid?> ReissueTemporaryPasswordAsync(BootstrapAccount account, CancellationToken cancellationToken)
    {
        var rows = await _db.Database.SqlQuery<Guid>(
            $"""
            update identity.users u
            set password_hash = {account.PasswordHash},
                security_stamp = {NewSecurityStamp()},
                concurrency_stamp = {Guid.NewGuid().ToString("D")},
                temporary_password_expires_at = {account.ExpiresAt},
                access_failed_count = 0,
                lockout_end = null
            where u.normalized_email = {account.NormalizedEmail}
              and u.temporary_password_expires_at is not null
              and exists (
                  select 1 from identity.user_roles ur join identity.roles r on r.id = ur.role_id
                  where ur.user_id = u.id and r.name = {account.Role})
            returning u.id as "Value"
            """).ToListAsync(cancellationToken);
        return rows.Count == 1 ? rows[0] : null;
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
