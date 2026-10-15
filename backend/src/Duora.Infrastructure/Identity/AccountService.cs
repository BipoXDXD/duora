using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Duora.Core.Identity;
using Duora.Infrastructure.Email;
using Duora.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Duora.Infrastructure.Identity;

/// <summary>Conta que provou a senha e pode abrir sessão.</summary>
public sealed record VerifiedAccount(Guid UserId, string SecurityStamp);

/// <summary>Resultado da verificação de credenciais. Os casos negativos não dizem se a conta existe.</summary>
public abstract record LoginOutcome
{
    private LoginOutcome()
    {
    }

    public sealed record Verified(VerifiedAccount Account) : LoginOutcome;

    /// <summary>Conta inexistente, senha errada, e-mail não confirmado ou bloqueio temporário: a mesma resposta.</summary>
    public sealed record InvalidCredentials : LoginOutcome;

    /// <summary>Senha correta numa conta com MFA; a segunda etapa ainda não existe (ADR-0017).</summary>
    public sealed record MfaRequired : LoginOutcome;
}

/// <summary>Conta do titular como aparece em <c>/me</c>.</summary>
public sealed record AccountSummary(
    Guid Id,
    string Email,
    AccountStatus Status,
    bool EmailConfirmed,
    bool MfaEnabled,
    IReadOnlyList<string> Roles);

/// <summary>Prazos do ciclo de vida da conta (seção <c>Identity</c>).</summary>
internal sealed class AccountLifecycleOptions
{
    public const string Section = "Identity";

    /// <summary>
    /// Quanto tempo um cadastro pode ficar sem confirmar o e-mail. Depois disso a confirmação é recusada; um
    /// cadastro novo do mesmo e-mail substitui o pendente a qualquer momento.
    /// </summary>
    public TimeSpan PendingAccountLifetime { get; set; } = TimeSpan.FromHours(72);

    public bool IsValid() => PendingAccountLifetime > TimeSpan.Zero;
}

/// <summary>
/// Casos de uso da conta sobre o ASP.NET Core Identity (spec 8.2). Toda resposta para quem não é o titular é
/// neutra, e os e-mails saem pela outbox na mesma transação que os justifica, nunca por chamada direta.
/// </summary>
public sealed class AccountService
{
    /// <summary>Mesma entropia do security stamp que o Identity gera (160 bits).</summary>
    private const int SecurityStampBytes = 20;

    private readonly DuoraDbContext _db;
    private readonly UserManager<DuoraUser> _users;
    private readonly SignInManager<DuoraUser> _signIn;
    private readonly LoginSessions _sessions;
    private readonly CredentialTiming _timing;
    private readonly AccountLifecycleOptions _lifecycle;
    private readonly TimeProvider _time;

    internal AccountService(
        DuoraDbContext db,
        UserManager<DuoraUser> users,
        SignInManager<DuoraUser> signIn,
        LoginSessions sessions,
        CredentialTiming timing,
        AccountLifecycleOptions lifecycle,
        TimeProvider time)
    {
        _db = db;
        _users = users;
        _signIn = signIn;
        _sessions = sessions;
        _timing = timing;
        _lifecycle = lifecycle;
        _time = time;
    }

    /// <summary>
    /// Cadastra o e-mail. Conta nova nasce pendente; conta pendente (e-mail nunca confirmado) é substituída por
    /// este cadastro, com senha, aceites e security stamp novos, o que invalida todo link de confirmação anterior
    /// (TM-66: quem cadastrou antes não fica com a conta de quem tem o e-mail). Conta confirmada não muda, e o
    /// titular recebe um aviso. Quem chamou não fica sabendo qual caso aconteceu.
    /// </summary>
    /// <remarks>
    /// Os três casos fazem o mesmo trabalho (TM-06): um hash, um upsert que grava uma versão da linha do usuário,
    /// os mesmos comandos de aceite, uma leitura, um token e uma linha de outbox, na mesma transação. O upsert com
    /// <c>ON CONFLICT</c> no e-mail normalizado também serializa cadastros simultâneos do mesmo e-mail.
    /// </remarks>
    public async Task RegisterAsync(
        EmailAddress email,
        string password,
        string termsVersion,
        string privacyVersion,
        CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        // O PasswordHasher padrão não usa o usuário; o hash é calculado antes de saber qual caso é este.
        var passwordHash = _users.PasswordHasher.HashPassword(null!, password);

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        var registration = await UpsertRegistrationAsync(email, passwordHash, now, cancellationToken);
        await ReplaceConsentsUnlessConfirmedAsync(registration, termsVersion, privacyVersion, now, cancellationToken);
        var user = await _users.FindByIdAsync(registration.UserId.ToString("D"))
            ?? throw new InvalidOperationException("Conta sumiu dentro da transação do cadastro.");
        var token = await _users.GenerateEmailConfirmationTokenAsync(user);
        if (registration.EmailConfirmed)
        {
            Enqueue(AccountEmailKind.RegistrationAttemptOnExistingAccount, email, user.Id, token: null);
        }
        else
        {
            Enqueue(AccountEmailKind.EmailConfirmation, email, user.Id, token);
        }

        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<bool> ConfirmEmailAsync(Guid userId, string linkToken, CancellationToken cancellationToken)
    {
        var user = await _users.FindByIdAsync(userId.ToString("D"));
        if (user is null || IsExpiredPendingRegistration(user) || !TryDecodeLinkToken(linkToken, out var token))
        {
            return false;
        }

        var result = await _users.ConfirmEmailAsync(user, token);
        if (!result.Succeeded)
        {
            return false;
        }

        user.CompleteEmailConfirmation();
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Conta pendente não recebe link de redefinição: redefinir não confirma o e-mail, porque o cadastro pendente
    /// pode ser de outra pessoa (com os aceites dela). Quem tem o e-mail recebe um lembrete para cadastrar de novo,
    /// o que substitui o pendente (TM-66).
    /// </summary>
    public async Task RequestPasswordResetAsync(EmailAddress email, CancellationToken cancellationToken)
    {
        var user = await _users.FindByEmailAsync(email.Value);
        if (user is null)
        {
            return;
        }

        if (user.EmailConfirmed)
        {
            Enqueue(AccountEmailKind.PasswordReset, email, user.Id, await _users.GeneratePasswordResetTokenAsync(user));
        }
        else
        {
            Enqueue(AccountEmailKind.PendingRegistrationReminder, email, user.Id, token: null);
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Troca a senha com o token do e-mail. O Identity troca o security stamp, o que já invalida todas as sessões;
    /// a revogação explícita deixa o motivo registrado. O bloqueio por tentativas é zerado, para quem foi
    /// bloqueado por um atacante recuperar o acesso pelo e-mail. Só vale para conta confirmada.
    /// </summary>
    public async Task<bool> ResetPasswordAsync(
        Guid userId,
        string linkToken,
        string newPassword,
        CancellationToken cancellationToken)
    {
        var user = await _users.FindByIdAsync(userId.ToString("D"));
        if (user is null || !user.EmailConfirmed || !TryDecodeLinkToken(linkToken, out var token))
        {
            return false;
        }

        var result = await _users.ResetPasswordAsync(user, token, newPassword);
        if (!result.Succeeded)
        {
            return false;
        }

        await _users.ResetAccessFailedCountAsync(user);
        await _users.SetLockoutEndDateAsync(user, lockoutEnd: null);
        await _sessions.RevokeAllAsync(user.Id, "password-reset", cancellationToken);
        return true;
    }

    public async Task<LoginOutcome> VerifyCredentialsAsync(
        EmailAddress email,
        string password,
        CancellationToken cancellationToken)
    {
        var user = await _users.FindByEmailAsync(email.Value);
        if (user is null)
        {
            _timing.SpendHashingTime(password);
            return new LoginOutcome.InvalidCredentials();
        }

        var result = await _signIn.CheckPasswordSignInAsync(user, password, lockoutOnFailure: true);
        if (result.IsNotAllowed || result.IsLockedOut)
        {
            // O SignInManager recusa e-mail não confirmado e conta bloqueada antes de calcular o hash.
            _timing.SpendHashingTime(password);
            return new LoginOutcome.InvalidCredentials();
        }

        if (!result.Succeeded)
        {
            return new LoginOutcome.InvalidCredentials();
        }

        return user.TwoFactorEnabled
            ? new LoginOutcome.MfaRequired()
            : new LoginOutcome.Verified(new VerifiedAccount(user.Id, await _users.GetSecurityStampAsync(user)));
    }

    public Task<AccountSummary?> FindAccountAsync(Guid userId, CancellationToken cancellationToken) =>
        _db.Users
            .AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => new AccountSummary(
                user.Id,
                user.Email ?? "",
                user.Status,
                user.EmailConfirmed,
                user.TwoFactorEnabled,
                _db.UserRoles
                    .Where(link => link.UserId == user.Id)
                    .Join(_db.Roles, link => link.RoleId, role => role.Id, (_, role) => role.Name ?? "")
                    .OrderBy(name => name)
                    .ToList()))
            .SingleOrDefaultAsync(cancellationToken);

    private async Task<RegistrationRow> UpsertRegistrationAsync(
        EmailAddress email,
        string passwordHash,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var normalizedEmail = _users.NormalizeEmail(email.Value);
        var newUserId = Guid.CreateVersion7(now);
        var securityStamp = Convert.ToHexString(RandomNumberGenerator.GetBytes(SecurityStampBytes));
        var concurrencyStamp = Guid.NewGuid().ToString("D");
        var pendingEmail = nameof(AccountStatus.PendingEmail);

        // Conta confirmada: o UPDATE regrava os mesmos valores, para esse caso tocar o banco como os outros.
        // Conta pendente: tudo o que vem de quem cadastrou (senha, stamp, e-mail digitado, data) é trocado.
        var rows = await _db.Database.SqlQuery<RegistrationRow>(
            $"""
            insert into identity.users as u
                (id, user_name, normalized_user_name, email, normalized_email, email_confirmed, password_hash,
                 security_stamp, concurrency_stamp, phone_number_confirmed, two_factor_enabled, lockout_enabled,
                 access_failed_count, status, created_at)
            values
                ({newUserId}, {email.Value}, {normalizedEmail}, {email.Value}, {normalizedEmail}, false, {passwordHash},
                 {securityStamp}, {concurrencyStamp}, false, false, true,
                 0, {pendingEmail}, {now})
            on conflict (normalized_email) do update set
                user_name = case when u.email_confirmed then u.user_name else excluded.user_name end,
                email = case when u.email_confirmed then u.email else excluded.email end,
                password_hash = case when u.email_confirmed then u.password_hash else excluded.password_hash end,
                security_stamp = case when u.email_confirmed then u.security_stamp else excluded.security_stamp end,
                concurrency_stamp = case when u.email_confirmed then u.concurrency_stamp else excluded.concurrency_stamp end,
                two_factor_enabled = case when u.email_confirmed then u.two_factor_enabled else false end,
                access_failed_count = case when u.email_confirmed then u.access_failed_count else 0 end,
                lockout_end = case when u.email_confirmed then u.lockout_end else null end,
                created_at = case when u.email_confirmed then u.created_at else excluded.created_at end
            returning u.id as "UserId", u.email_confirmed as "EmailConfirmed"
            """).ToListAsync(cancellationToken);
        return rows.Single();
    }

    /// <summary>Os aceites passam a ser os de quem fez este cadastro; numa conta confirmada, nada muda.</summary>
    private async Task ReplaceConsentsUnlessConfirmedAsync(
        RegistrationRow registration,
        string termsVersion,
        string privacyVersion,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var replace = !registration.EmailConfirmed;
        var terms = nameof(LegalDocument.Terms);
        var privacy = nameof(LegalDocument.Privacy);
        await _db.Database.ExecuteSqlAsync(
            $"delete from identity.consent_records where user_id = {registration.UserId} and {replace}",
            cancellationToken);
        await _db.Database.ExecuteSqlAsync(
            $"""
            insert into identity.consent_records (id, user_id, document, version, accepted_at)
            select v.id, {registration.UserId}, v.document, v.version, {now}
            from (values ({Guid.CreateVersion7(now)}, {terms}, {termsVersion}),
                         ({Guid.CreateVersion7(now)}, {privacy}, {privacyVersion})) as v(id, document, version)
            where {replace}
            """,
            cancellationToken);
    }

    private bool IsExpiredPendingRegistration(DuoraUser user) =>
        !user.EmailConfirmed && user.CreatedAt + _lifecycle.PendingAccountLifetime <= _time.GetUtcNow();

    private void Enqueue(AccountEmailKind kind, EmailAddress email, Guid userId, string? token)
    {
        var linkToken = token is null ? null : Base64Url.EncodeToString(Encoding.UTF8.GetBytes(token));
        _db.Add(AccountEmailOutbox.Create(new AccountEmailRequested(kind, email.Value, userId, linkToken), _time.GetUtcNow()));
    }

    /// <summary>O token do Identity tem '+', '/' e '='; no link ele vai como base64url dos seus bytes UTF-8.</summary>
    private static bool TryDecodeLinkToken(string linkToken, out string token)
    {
        token = "";
        if (!Base64Url.IsValid(linkToken))
        {
            return false;
        }

        token = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(linkToken));
        return token.Length > 0;
    }

    private sealed record RegistrationRow(Guid UserId, bool EmailConfirmed);
}

/// <summary>
/// Gasta o tempo de um hash de senha quando o caminho real não calcularia nenhum (conta inexistente, bloqueada ou
/// não confirmada), para o tempo de resposta não revelar a existência da conta.
/// </summary>
/// <remarks>
/// Usa o mesmo <see cref="PasswordHasher{TUser}"/> do Identity, com as mesmas opções (algoritmo e iterações), mas
/// numa instância própria: o do Identity é scoped e este hash de isca é calculado uma vez por processo. O
/// PasswordHasher não usa o usuário, por isso passar null é seguro.
/// </remarks>
internal sealed class CredentialTiming(IOptions<PasswordHasherOptions> options)
{
    private readonly PasswordHasher<DuoraUser> _hasher = new(options);
    private string? _decoyHash;

    public void SpendHashingTime(string password)
    {
        // Corrida inofensiva: duas threads podem calcular iscas diferentes; qualquer uma serve.
        _decoyHash ??= _hasher.HashPassword(null!, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        _hasher.VerifyHashedPassword(null!, _decoyHash, password);
    }
}
