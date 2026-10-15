using System.Buffers.Text;
using System.Diagnostics;
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

    /// <summary>Falhas de confirmação (token ou senha) que invalidam o link de uma conta pendente.</summary>
    public int MaxConfirmationFailures { get; set; } = 5;

    /// <summary>
    /// Duração mínima de um login que falha, mais um sorteio de até <see cref="FailedLoginJitter"/>. Fica acima
    /// do custo de um hash, para encobrir o que os caminhos de falha ainda fazem de diferente (a senha errada numa
    /// conta existente grava a falha para o bloqueio; os outros casos não gravam) (TM-06).
    /// </summary>
    public TimeSpan FailedLoginMinimumDuration { get; set; } = TimeSpan.FromMilliseconds(350);

    public TimeSpan FailedLoginJitter { get; set; } = TimeSpan.FromMilliseconds(100);

    public bool IsValid() =>
        PendingAccountLifetime > TimeSpan.Zero
        && MaxConfirmationFailures >= 1
        && FailedLoginMinimumDuration >= TimeSpan.Zero
        && FailedLoginJitter >= TimeSpan.Zero;
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
    private readonly DecoyPasswordHash _decoy;
    private readonly AccountLifecycleOptions _lifecycle;
    private readonly TimeProvider _time;

    internal AccountService(
        DuoraDbContext db,
        UserManager<DuoraUser> users,
        SignInManager<DuoraUser> signIn,
        LoginSessions sessions,
        DecoyPasswordHash decoy,
        AccountLifecycleOptions lifecycle,
        TimeProvider time)
    {
        _db = db;
        _users = users;
        _signIn = signIn;
        _sessions = sessions;
        _decoy = decoy;
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
            Enqueue(AccountEmailKind.RegistrationAttemptOnExistingAccount, StoredAddress(user), user.Id, token: null);
        }
        else
        {
            Enqueue(AccountEmailKind.EmailConfirmation, StoredAddress(user), user.Id, token);
        }

        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Confirma o e-mail só com o link (posse do e-mail) e a senha do cadastro pendente (conhecimento da senha).
    /// Assim um link gerado por outra pessoa, que chegou à caixa da vítima, não confirma a conta dela com a senha
    /// de outra pessoa (TM-66). Senha errada e token inválido dão o mesmo resultado, e toda tentativa custa um hash.
    /// Depois de <see cref="AccountLifecycleOptions.MaxConfirmationFailures"/> falhas numa conta pendente, o stamp
    /// muda e o link deixa de valer: a pessoa cadastra de novo.
    /// </summary>
    public async Task<bool> ConfirmEmailAsync(
        Guid userId,
        string linkToken,
        string password,
        CancellationToken cancellationToken)
    {
        var user = await _users.FindByIdAsync(userId.ToString("D"));
        if (user is null)
        {
            VerifyDecoy(password);
            return false;
        }

        var passwordMatches = user.PasswordHash is { } hash
            && _users.PasswordHasher.VerifyHashedPassword(user, hash, password) != PasswordVerificationResult.Failed;
        var tokenIsValid = !IsExpiredPendingRegistration(user)
            && TryDecodeLinkToken(linkToken, out var token)
            && await _users.VerifyUserTokenAsync(
                user,
                _users.Options.Tokens.EmailConfirmationTokenProvider,
                UserManager<DuoraUser>.ConfirmEmailTokenPurpose,
                token);
        if (!passwordMatches || !tokenIsValid)
        {
            await CountConfirmationFailureAsync(user);
            return false;
        }

        if (!user.EmailConfirmed)
        {
            user.EmailConfirmed = true;
            user.CompleteEmailConfirmation();
            await _users.ResetAccessFailedCountAsync(user);
        }

        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Todo pedido gera exatamente um e-mail, para os três casos fazerem o mesmo trabalho (TM-06): conta
    /// confirmada recebe o link; conta pendente recebe um lembrete para cadastrar de novo, porque redefinir não
    /// confirma o e-mail e o cadastro pendente pode ser de outra pessoa (TM-66); endereço sem conta recebe um
    /// aviso de que não há conta. O excesso de e-mails é contido pelo rate limit por endereço.
    /// </summary>
    public async Task RequestPasswordResetAsync(EmailAddress email, CancellationToken cancellationToken)
    {
        var user = await _users.FindByEmailAsync(email.Value);
        switch (user)
        {
            case null:
                Enqueue(AccountEmailKind.PasswordResetForUnknownAddress, email.Value, Guid.CreateVersion7(_time.GetUtcNow()), token: null);
                break;
            case { EmailConfirmed: true }:
                Enqueue(AccountEmailKind.PasswordReset, StoredAddress(user), user.Id, await _users.GeneratePasswordResetTokenAsync(user));
                break;
            default:
                Enqueue(AccountEmailKind.PendingRegistrationReminder, StoredAddress(user), user.Id, token: null);
                break;
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

    /// <summary>
    /// Verifica a senha. Toda falha custa um hash e não responde antes do piso de latência (TM-06): conta
    /// inexistente, e-mail não confirmado e conta bloqueada verificam um hash de isca, porque nesses casos o
    /// SignInManager decide antes de calcular o hash.
    /// </summary>
    public async Task<LoginOutcome> VerifyCredentialsAsync(
        EmailAddress email,
        string password,
        CancellationToken cancellationToken)
    {
        var startedAt = Stopwatch.GetTimestamp();
        var outcome = await CheckCredentialsAsync(email, password);
        if (outcome is LoginOutcome.InvalidCredentials)
        {
            await WaitForFailureFloorAsync(startedAt, cancellationToken);
        }

        return outcome;
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

    private async Task<LoginOutcome> CheckCredentialsAsync(EmailAddress email, string password)
    {
        var user = await _users.FindByEmailAsync(email.Value);
        if (user is null)
        {
            VerifyDecoy(password);
            return new LoginOutcome.InvalidCredentials();
        }

        var result = await _signIn.CheckPasswordSignInAsync(user, password, lockoutOnFailure: true);
        if (result.IsNotAllowed || result.IsLockedOut)
        {
            VerifyDecoy(password);
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

    /// <summary>
    /// Mesmo hasher (e mesmo custo) que o SignInManager usa na senha real. O PasswordHasher padrão não usa o
    /// usuário, por isso passar null é seguro.
    /// </summary>
    private void VerifyDecoy(string password) =>
        _users.PasswordHasher.VerifyHashedPassword(null!, _decoy.Value, password);

    /// <summary>
    /// Espera em tempo real, não no <see cref="TimeProvider"/> da aplicação: o piso protege contra quem mede a
    /// resposta de fora, e um relógio falso em teste não pode congelá-lo.
    /// </summary>
    private async Task WaitForFailureFloorAsync(long startedAt, CancellationToken cancellationToken)
    {
        var floor = _lifecycle.FailedLoginMinimumDuration + (_lifecycle.FailedLoginJitter * Random.Shared.NextDouble());
        var remaining = floor - Stopwatch.GetElapsedTime(startedAt);
        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining, cancellationToken);
        }
    }

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

    /// <summary>
    /// Usa o contador de falhas do Identity, que numa conta pendente não serve a mais nada (ela não faz login) e é
    /// zerado na confirmação. Conta já confirmada não conta aqui: o lockout dela é o do login.
    /// </summary>
    private async Task CountConfirmationFailureAsync(DuoraUser user)
    {
        if (user.EmailConfirmed)
        {
            return;
        }

        await _users.AccessFailedAsync(user);
        if (await _users.GetAccessFailedCountAsync(user) >= _lifecycle.MaxConfirmationFailures)
        {
            await _users.UpdateSecurityStampAsync(user);
            await _users.ResetAccessFailedCountAsync(user);
        }
    }

    private bool IsExpiredPendingRegistration(DuoraUser user) =>
        !user.EmailConfirmed && user.CreatedAt + _lifecycle.PendingAccountLifetime <= _time.GetUtcNow();

    /// <summary>
    /// E-mail de conta vai para o endereço gravado, nunca para o digitado: a busca é por e-mail normalizado, e o
    /// que foi digitado pode ser uma variante que só coincide na normalização (TM-67).
    /// </summary>
    private static string StoredAddress(DuoraUser user) =>
        user.Email ?? throw new InvalidOperationException("Conta sem e-mail gravado.");

    private void Enqueue(AccountEmailKind kind, string recipient, Guid userId, string? token)
    {
        var linkToken = token is null ? null : Base64Url.EncodeToString(Encoding.UTF8.GetBytes(token));
        _db.Add(AccountEmailOutbox.Create(new AccountEmailRequested(kind, recipient, userId, linkToken), _time.GetUtcNow()));
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
/// Hash de uma senha aleatória, calculado uma vez por processo com as opções do Identity (algoritmo e iterações).
/// Verificá-lo custa o mesmo que verificar uma senha real, sem nunca conferir.
/// </summary>
internal sealed class DecoyPasswordHash(IOptions<PasswordHasherOptions> options)
{
    private readonly Lazy<string> _value = new(() =>
        new PasswordHasher<DuoraUser>(options).HashPassword(null!, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));

    public string Value => _value.Value;
}
