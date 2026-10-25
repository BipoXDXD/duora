using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Duora.Core.Identity;
using Duora.Core.Notifications;
using Duora.Infrastructure.Audit;
using Duora.Infrastructure.Email;
using Duora.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Duora.Infrastructure.Identity;

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

    public bool IsValid() => PendingAccountLifetime > TimeSpan.Zero && MaxConfirmationFailures >= 1;
}

/// <summary>
/// Casos de uso da conta sobre o ASP.NET Core Identity (spec 8.2). Toda resposta para quem não é o titular é
/// neutra, e os e-mails saem pela outbox na mesma transação que os justifica, nunca por chamada direta.
/// </summary>
public sealed class AccountService
{
    /// <summary>Mesma entropia do security stamp que o Identity gera (160 bits).</summary>
    private const int SecurityStampBytes = 20;
    private const string ConfirmationCodeProvider = "Duora.EmailConfirmation";
    private const string ConfirmationCodeName = "CodeHash";
    private static readonly TimeSpan ConfirmationCodeLifetime = TimeSpan.FromMinutes(15);

    private readonly DuoraDbContext _db;
    private readonly UserManager<DuoraUser> _users;
    private readonly AuditTrail _audit;
    private readonly AccountSecurityService _security;
    private readonly DecoyPasswordHash _decoy;
    private readonly AccountLifecycleOptions _lifecycle;
    private readonly TimeProvider _time;
    private readonly LegalPublication _legal;

    internal AccountService(
        DuoraDbContext db,
        UserManager<DuoraUser> users,
        AccountSecurityService security,
        DecoyPasswordHash decoy,
        AccountLifecycleOptions lifecycle,
        TimeProvider time,
        LegalPublication legal)
    {
        _db = db;
        _users = users;
        _security = security;
        _audit = new AuditTrail(db, time);
        _decoy = decoy;
        _lifecycle = lifecycle;
        _time = time;
        _legal = legal;
    }

    /// <summary>
    /// Cadastra o e-mail. Conta nova nasce pendente; conta pendente (e-mail nunca confirmado) é substituída por
    /// este cadastro, com senha, aceites e security stamp novos, o que invalida todo link de confirmação anterior
    /// (TM-66: quem cadastrou antes não fica com a conta de quem tem o e-mail). Conta confirmada não muda, e o
    /// titular recebe um aviso. Quem chamou não fica sabendo qual caso aconteceu.
    /// </summary>
    /// <remarks>
    /// Os três casos fazem o mesmo trabalho (TM-06): um hash, um upsert que grava uma versão da linha do usuário,
    /// os mesmos comandos de aceite, uma leitura, um hash de código e uma linha de outbox, na mesma transação. O upsert com
    /// <c>ON CONFLICT</c> no e-mail normalizado também serializa cadastros simultâneos do mesmo e-mail.
    /// </remarks>
    public async Task RegisterAsync(
        EmailAddress email,
        string password,
        string termsVersion,
        string privacyVersion,
        EmailLanguage language,
        CancellationToken cancellationToken,
        string? termsLocale = null,
        string? privacyLocale = null,
        string? termsSha256 = null,
        string? privacySha256 = null)
    {
        if (!_legal.Accepts(termsVersion, privacyVersion, termsLocale, privacyLocale, termsSha256, privacySha256))
        {
            throw new InvalidOperationException("Published legal documents and exact acceptance are required before registration.");
        }
        var now = _time.GetUtcNow();
        // O PasswordHasher padrão não usa o usuário; o hash é calculado antes de saber qual caso é este.
        var passwordHash = _users.PasswordHasher.HashPassword(null!, password);

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        var registration = await UpsertRegistrationAsync(email, passwordHash, EmailLanguageTag.Format(language), now, cancellationToken);
        if (!await ReserveIssuanceAsync(registration.UserId, now, cancellationToken))
        {
            // Roll back the upsert too: a throttled registration must not replace the current proof/password.
            return;
        }

        await ReplaceConsentsUnlessConfirmedAsync(registration, termsVersion, privacyVersion, termsLocale, privacyLocale, termsSha256!, privacySha256!, now, cancellationToken);
        await GrantParticipantRoleUnlessAnyAsync(registration.UserId, cancellationToken);
        var user = await _users.FindByIdAsync(registration.UserId.ToString("D"))
            ?? throw new InvalidOperationException("Conta sumiu dentro da transação do cadastro.");
        var code = RandomNumberGenerator.GetInt32(100_000_000).ToString("D8", CultureInfo.InvariantCulture);
        var codeHash = _users.PasswordHasher.HashPassword(user, code);
        var codeValue = $"{user.SecurityStamp}|{now.UtcTicks.ToString(CultureInfo.InvariantCulture)}|{codeHash}";
        var storeCode = !registration.EmailConfirmed;
        await _db.Database.ExecuteSqlAsync(
            $"""
            insert into identity.user_tokens (user_id, login_provider, name, value)
            select {user.Id}, {ConfirmationCodeProvider}, {ConfirmationCodeName}, {codeValue}
            where {storeCode}
            on conflict (user_id, login_provider, name) do update set value = excluded.value
            """, cancellationToken);
        if (registration.EmailConfirmed)
        {
            Enqueue(AccountEmailKind.RegistrationAttemptOnExistingAccount, StoredAddress(user), user.Id, token: null, language);
        }
        else
        {
            // Mesmo envelope da outbox; o código numérico não é um link/token Data Protection.
            _db.Add(AccountEmailOutbox.Create(new AccountEmailRequested(
                AccountEmailKind.EmailConfirmation, StoredAddress(user), user.Id, code,
                EmailLanguageTag.Format(language)), now));
        }

        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Emite outro código somente para o cadastro pendente ainda admissível e com a senha correta. Não cria,
    /// substitui ou confirma conta, nem renova sua idade/aceites. Registro e reenvio compartilham o orçamento
    /// persistente. A linha relida sob a mesma trava da confirmação impede emitir depois de confirmar.
    /// </summary>
    public async Task RequestEmailConfirmationAsync(EmailAddress email, string password, CancellationToken cancellationToken)
    {
        var code = RandomNumberGenerator.GetInt32(100_000_000).ToString("D8", CultureInfo.InvariantCulture);
        // Como no cadastro, o hasher não depende do usuário: todos os estados custam este hash e uma prova de senha.
        var codeHash = _users.PasswordHasher.HashPassword(null!, code);
        var user = await _users.FindByEmailAsync(email.Value);
        if (user is null)
        {
            VerifyDecoy(password);
            return;
        }

        await using var check = await _security.LockAccountAsync(user, cancellationToken);
        var passwordMatches = false;
        if (user.PasswordHash is { } passwordHash)
        {
            passwordMatches = _users.PasswordHasher.VerifyHashedPassword(user, passwordHash, password) != PasswordVerificationResult.Failed;
        }
        else
        {
            VerifyDecoy(password);
        }
        if (!passwordMatches || user.EmailConfirmed || user.Status != AccountStatus.PendingEmail || IsExpiredPendingRegistration(user))
        {
            return;
        }

        var now = _time.GetUtcNow();
        if (!await ReserveIssuanceAsync(user.Id, now, cancellationToken))
        {
            return;
        }
        // A troca invalida também links Data Protection legados; nenhuma sessão é aberta para uma conta pendente.
        var stamp = NewSecurityStamp();
        var changed = await _db.Database.ExecuteSqlAsync($"""
            update identity.users set security_stamp={stamp},concurrency_stamp={Guid.NewGuid().ToString("D")}
            where id={user.Id} and not email_confirmed and status='PendingEmail' and security_stamp={user.SecurityStamp}
            """, cancellationToken);
        if (changed != 1)
        {
            return;
        }
        var value = $"{stamp}|{now.UtcTicks.ToString(CultureInfo.InvariantCulture)}|{codeHash}";
        await _db.Database.ExecuteSqlAsync($"""
            insert into identity.user_tokens(user_id,login_provider,name,value)
            values({user.Id},{ConfirmationCodeProvider},{ConfirmationCodeName},{value})
            on conflict(user_id,login_provider,name) do update set value=excluded.value
            """, cancellationToken);
        _db.Add(AccountEmailOutbox.Create(new AccountEmailRequested(
            AccountEmailKind.EmailConfirmation, StoredAddress(user), user.Id, code,
            user.PreferredLocale ?? EmailLanguageTag.Format(EmailLanguageTag.Default)), now));
        await _db.SaveChangesAsync(cancellationToken);
        await check.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Confirma o e-mail com o código (ou link legado, posse do e-mail) e a senha do cadastro pendente (conhecimento da senha).
    /// Assim um link gerado por outra pessoa, que chegou à caixa da vítima, não confirma a conta dela com a senha
    /// de outra pessoa (TM-66). Senha errada e token inválido dão o mesmo resultado, e toda tentativa custa um hash.
    /// </summary>
    /// <remarks>
    /// Confirmar troca o security stamp: o link morre na hora e nunca serve de oráculo de senha depois da
    /// confirmação. Uma conta pendente não tem sessão (não faz login), então a troca não derruba ninguém. A gravação
    /// é serializada com a conta travada e relida antes de verificar a prova e contar falhas. Um recadastro
    /// que ganha a trava invalida a prova anterior; confirmação e consumo do código têm um commit só.
    /// </remarks>
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

        await using var check = await _security.LockAccountAsync(user, cancellationToken);
        var verifiedStamp = user.SecurityStamp ?? "";
        var passwordMatches = user.PasswordHash is { } hash
            && _users.PasswordHasher.VerifyHashedPassword(user, hash, password) != PasswordVerificationResult.Failed;
        var tokenIsValid = !user.EmailConfirmed
            && !IsExpiredPendingRegistration(user)
            && await VerifyConfirmationProofAsync(user, linkToken);
        if (!passwordMatches || !tokenIsValid)
        {
            await CountConfirmationFailureAsync(user.Id, verifiedStamp, cancellationToken);
            await check.CommitAsync(cancellationToken);
            return false;
        }

        var confirmed = await MarkEmailConfirmedAsync(user.Id, verifiedStamp, cancellationToken);
        await check.CommitAsync(cancellationToken);
        return confirmed;
    }

    /// <summary>
    /// Todo pedido gera exatamente um e-mail, para os casos fazerem o mesmo trabalho (TM-06): conta confirmada
    /// recebe o link; conta pendente recebe um lembrete para cadastrar de novo, porque redefinir não confirma o
    /// e-mail e o cadastro pendente pode ser de outra pessoa (TM-66); endereço sem conta recebe um aviso de que não
    /// há conta. Conta privilegiada que ainda não ativou o MFA recebe um aviso sem link (I5): antes do segundo fator,
    /// o e-mail sozinho não pode virar caminho para tomar a conta. O excesso de e-mails é contido pelo rate limit
    /// por endereço.
    /// </summary>
    public async Task RequestPasswordResetAsync(
        EmailAddress email,
        EmailLanguage language,
        CancellationToken cancellationToken)
    {
        var user = await _users.FindByEmailAsync(email.Value);
        // A consulta de papel roda em todos os casos, até sem conta, para o trabalho continuar igual (TM-06).
        var awaitsFirstMfa = await HasPrivilegedRoleAsync(user?.Id ?? Guid.Empty, cancellationToken)
            && user is { TwoFactorEnabled: false };
        switch (user)
        {
            case null:
                Enqueue(
                    AccountEmailKind.PasswordResetForUnknownAddress,
                    email.Value,
                    Guid.CreateVersion7(_time.GetUtcNow()),
                    token: null,
                    language);
                break;
            case { EmailConfirmed: true } when awaitsFirstMfa:
                Enqueue(AccountEmailKind.PasswordResetUnavailable, StoredAddress(user), user.Id, token: null, language);
                break;
            case { EmailConfirmed: true }:
                Enqueue(
                    AccountEmailKind.PasswordReset,
                    StoredAddress(user),
                    user.Id,
                    await _users.GeneratePasswordResetTokenAsync(user),
                    language);
                break;
            default:
                Enqueue(AccountEmailKind.PendingRegistrationReminder, StoredAddress(user), user.Id, token: null, language);
                break;
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Troca a senha com o token do e-mail. O Identity troca o security stamp, o que já invalida todas as sessões;
    /// a revogação explícita deixa o motivo registrado. O bloqueio por tentativas é zerado, para quem foi
    /// bloqueado por um atacante recuperar o acesso pelo e-mail, exceto em conta com MFA: o e-mail não prova o
    /// segundo fator, e destravar ali reabriria a força bruta do TOTP. Só vale para conta confirmada e, se
    /// privilegiada, com o MFA já ativado (I5). Troca, desbloqueio, revogação, auditoria (TM-05) e aviso ao titular
    /// (TM-21) vão num commit só, com a conta travada como nas outras operações de credencial: sem troca, nada é
    /// gravado, e uma falha de login simultânea não se perde nem derruba o reset.
    /// </summary>
    /// <exception cref="AccountBusyException">A espera pela conta passou do teto; nada foi gravado.</exception>
    public async Task<bool> ResetPasswordAsync(
        Guid userId,
        string linkToken,
        string newPassword,
        EmailLanguage language,
        CancellationToken cancellationToken)
    {
        var user = await _users.FindByIdAsync(userId.ToString("D"));
        if (user is null
            || !user.EmailConfirmed
            || (!user.TwoFactorEnabled && await HasPrivilegedRoleAsync(user.Id, cancellationToken))
            || !TryDecodeLinkToken(linkToken, out var token))
        {
            return false;
        }

        await using var check = await _security.LockAccountAsync(user, cancellationToken);
        var wasLockedOut = await _users.IsLockedOutAsync(user);
        var result = await _users.ResetPasswordAsync(user, token, newPassword);
        if (!result.Succeeded)
        {
            return false;
        }

        await AppendAuditAsync(user.Id, AuditAction.PasswordReset, cancellationToken);
        if (!user.TwoFactorEnabled)
        {
            EnsureSucceeded(await _users.ResetAccessFailedCountAsync(user), "zerar as falhas de login");
            EnsureSucceeded(await _users.SetLockoutEndDateAsync(user, lockoutEnd: null), "encerrar o bloqueio");
            if (wasLockedOut)
            {
                await AppendAuditAsync(user.Id, AuditAction.LockoutCleared, cancellationToken);
            }
        }

        await RevokeAllSessionsAsync(user.Id, "password-reset", cancellationToken);
        _db.Add(AccountEmailOutbox.CreateSecurityNotice(
            SecurityChange.PasswordReset,
            StoredAddress(user),
            user.Id,
            language,
            _time.GetUtcNow(),
            user.PreferredTimeZone));
        await _db.SaveChangesAsync(cancellationToken);
        await check.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// O stamp novo já invalida as sessões; a revogação explícita deixa o motivo registrado, na transação da troca.
    /// </summary>
    private async Task RevokeAllSessionsAsync(Guid userId, string reason, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        await _db.Database.ExecuteSqlAsync(
            $"""
            update identity.login_sessions set revoked_at = {now}, revocation_reason = {reason}
            where user_id = {userId} and revoked_at is null
            """,
            cancellationToken);
    }

    /// <summary>O titular é ator e alvo: o reset prova a posse do e-mail, não uma sessão.</summary>
    private Task AppendAuditAsync(Guid userId, AuditAction action, CancellationToken cancellationToken) =>
        _audit.AppendAsync(
            new AuditRecord(AuditActorKind.User, userId, action, AuditTargetTypes.User, userId),
            cancellationToken);

    private static void EnsureSucceeded(IdentityResult result, string operation)
    {
        if (!result.Succeeded)
        {
            var codes = string.Join(", ", result.Errors.Select(error => error.Code));
            throw new InvalidOperationException($"O Identity recusou {operation}: {codes}.");
        }
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

    /// <summary>
    /// A conta existe? Para outros módulos conferirem um id vindo de fora sem ler <c>identity.users</c> nem receber
    /// dados da conta (Safety: alvo de bloqueio).
    /// </summary>
    public Task<bool> ExistsAsync(Guid userId, CancellationToken cancellationToken) =>
        _db.Users.AsNoTracking().AnyAsync(user => user.Id == userId, cancellationToken);

    /// <summary>
    /// Situação da conta, ou <c>null</c> se ela não existe. Para outros módulos decidirem pela regra deles sem ler
    /// <c>identity.users</c> (Media: reconferir quem envia foto sob a guarda do upload). Usa o mesmo
    /// <see cref="DuoraDbContext"/> do escopo, então enxerga a transação aberta por quem chama.
    /// </summary>
    public Task<AccountStatus?> FindStatusAsync(Guid userId, CancellationToken cancellationToken) =>
        _db.Users
            .AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => (AccountStatus?)user.Status)
            .SingleOrDefaultAsync(cancellationToken);

    /// <summary>
    /// A conta tem papel privilegiado? Com MFA inativo, isso quer dizer que ela ainda não fez a primeira ativação
    /// (I5), porque conta privilegiada não desativa o MFA (A15); até lá, o reset fica bloqueado. Um comando só, com
    /// o mesmo texto para qualquer id, inclusive o de conta inexistente.
    /// </summary>
    private async Task<bool> HasPrivilegedRoleAsync(Guid userId, CancellationToken cancellationToken)
    {
        var privileged = AccountRoles.Privileged.ToArray();
        return await _db.Database.SqlQuery<bool>(
                $"""
                select exists (
                    select 1 from identity.user_roles ur join identity.roles r on r.id = ur.role_id
                    where ur.user_id = {userId} and r.name = any({privileged})) as "Value"
                """)
            .SingleAsync(cancellationToken);
    }

    /// <summary>Mesmo hasher (e mesmo custo) da senha real, para a conta inexistente custar um hash.</summary>
    private void VerifyDecoy(string password) => _decoy.Verify(_users.PasswordHasher, password);

    /// <summary>
    /// Conta nova nasce com o papel de participante (D13), na mesma transação do cadastro. Conta que já tem papel,
    /// inclusive privilegiada, não ganha nada: o INSERT não seleciona linha e o trigger nem dispara. O mesmo comando
    /// roda nos três casos do cadastro, para o trabalho continuar igual (TM-06).
    /// </summary>
    private async Task GrantParticipantRoleUnlessAnyAsync(Guid userId, CancellationToken cancellationToken)
    {
        var participant = AccountRoles.Participant;
        await _db.Database.ExecuteSqlAsync(
            $"""
            insert into identity.user_roles (user_id, role_id)
            select {userId}, r.id from identity.roles r
            where r.name = {participant}
              and not exists (select 1 from identity.user_roles ur where ur.user_id = {userId})
            """,
            cancellationToken);
    }

    private async Task<RegistrationRow> UpsertRegistrationAsync(
        EmailAddress email,
        string passwordHash,
        string locale,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var normalizedEmail = _users.NormalizeEmail(email.Value);
        var newUserId = Guid.CreateVersion7(now);
        var securityStamp = NewSecurityStamp();
        var concurrencyStamp = Guid.NewGuid().ToString("D");
        var pendingEmail = nameof(AccountStatus.PendingEmail);

        // Conta confirmada: o UPDATE regrava os mesmos valores, para esse caso tocar o banco como os outros.
        // Conta pendente: tudo o que vem de quem cadastrou (senha, stamp, e-mail digitado, data) é trocado.
        var rows = await _db.Database.SqlQuery<RegistrationRow>(
            $"""
            insert into identity.users as u
                (id, user_name, normalized_user_name, email, normalized_email, email_confirmed, password_hash,
                 security_stamp, concurrency_stamp, phone_number_confirmed, two_factor_enabled, lockout_enabled,
                 access_failed_count, status, created_at, preferred_locale)
            values
                ({newUserId}, {email.Value}, {normalizedEmail}, {email.Value}, {normalizedEmail}, false, {passwordHash},
                 {securityStamp}, {concurrencyStamp}, false, false, true,
                 0, {pendingEmail}, {now}, {locale})
            on conflict (normalized_email) do update set
                user_name = case when u.email_confirmed then u.user_name else excluded.user_name end,
                email = case when u.email_confirmed then u.email else excluded.email end,
                password_hash = case when u.email_confirmed then u.password_hash else excluded.password_hash end,
                security_stamp = case when u.email_confirmed then u.security_stamp else excluded.security_stamp end,
                concurrency_stamp = case when u.email_confirmed then u.concurrency_stamp else excluded.concurrency_stamp end,
                two_factor_enabled = case when u.email_confirmed then u.two_factor_enabled else false end,
                access_failed_count = case when u.email_confirmed then u.access_failed_count else 0 end,
                lockout_end = case when u.email_confirmed then u.lockout_end else null end,
                created_at = case when u.email_confirmed then u.created_at else excluded.created_at end,
                preferred_locale = case when u.email_confirmed then u.preferred_locale else excluded.preferred_locale end
            returning u.id as "UserId", u.email_confirmed as "EmailConfirmed"
            """).ToListAsync(cancellationToken);
        return rows.Single();
    }

    /// <summary>Os aceites passam a ser os de quem fez este cadastro; numa conta confirmada, nada muda.</summary>
    private async Task ReplaceConsentsUnlessConfirmedAsync(
        RegistrationRow registration,
        string termsVersion,
        string privacyVersion,
        string? termsLocale,
        string? privacyLocale,
        string termsSha256,
        string privacySha256,
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
            insert into identity.consent_records (id, user_id, document, version, accepted_at, document_locale, document_sha256)
            select v.id, {registration.UserId}, v.document, v.version, {now}, v.document_locale, v.document_sha256
            from (values ({Guid.CreateVersion7(now)}, {terms}, {termsVersion}, {termsLocale}, {termsSha256}),
                         ({Guid.CreateVersion7(now)}, {privacy}, {privacyVersion}, {privacyLocale}, {privacySha256})) as v(id, document, version, document_locale, document_sha256)
            where {replace}
            """,
            cancellationToken);
    }

    /// <summary>
    /// Pendente vira confirmada (estado <c>PendingEligibility</c>, spec 4.2) só se o stamp ainda for o verificado;
    /// o stamp novo invalida o link. Falso quando outro comando mudou a conta no meio.
    /// </summary>
    private async Task<bool> MarkEmailConfirmedAsync(Guid userId, string verifiedStamp, CancellationToken cancellationToken)
    {
        var pendingEmail = nameof(AccountStatus.PendingEmail);
        var pendingEligibility = nameof(AccountStatus.PendingEligibility);
        var updated = await _db.Database.ExecuteSqlAsync(
            $"""
            update identity.users
            set email_confirmed = true,
                status = case when status = {pendingEmail} then {pendingEligibility} else status end,
                access_failed_count = 0,
                security_stamp = {NewSecurityStamp()},
                concurrency_stamp = {Guid.NewGuid().ToString("D")}
            where id = {userId} and security_stamp = {verifiedStamp} and not email_confirmed
            """,
            cancellationToken);
        if (updated == 1)
        {
            await _db.Database.ExecuteSqlAsync(
                $"""
                delete from identity.user_tokens
                where user_id = {userId} and login_provider = {ConfirmationCodeProvider} and name = {ConfirmationCodeName}
                """, cancellationToken);
        }

        return updated == 1;
    }

    /// <summary>
    /// Conta a falha num único UPDATE atômico (sem ler e gravar de volta, que tentativas paralelas furariam) e, na
    /// falha que atinge o teto, troca o stamp no mesmo comando: o link morre e a pessoa cadastra de novo. Usa o
    /// contador do Identity, que numa conta pendente não serve a mais nada; conta confirmada não conta aqui (o
    /// lockout dela é o do login).
    /// </summary>
    private async Task CountConfirmationFailureAsync(Guid userId, string verifiedStamp, CancellationToken cancellationToken)
    {
        var ceiling = _lifecycle.MaxConfirmationFailures;
        await _db.Database.ExecuteSqlAsync(
            $"""
            update identity.users
            set access_failed_count = case when access_failed_count + 1 >= {ceiling} then 0 else access_failed_count + 1 end,
                security_stamp = case when access_failed_count + 1 >= {ceiling} then {NewSecurityStamp()} else security_stamp end,
                concurrency_stamp = {Guid.NewGuid().ToString("D")}
            where id = {userId} and security_stamp = {verifiedStamp} and not email_confirmed
            """,
            cancellationToken);
    }

    private async Task<bool> ReserveIssuanceAsync(Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        // The caller owns the user row lock. Registration/resend share this restart-safe budget.
        const string Provider = "Duora.RegistrationBudget";
        const string Name = "Hourly";
        var values = await _db.Database.SqlQuery<string>($"""
            select value as "Value" from identity.user_tokens
            where user_id = {userId} and login_provider = {Provider} and name = {Name}
            """).ToListAsync(ct);
        var start = now.UtcTicks;
        var count = 0;
        if (values.Count != 0)
        {
            var parts = values[0].Split('|');
            if (parts.Length != 2
                || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out start)
                || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out count))
            {
                return false;
            }

            if (start <= now.AddHours(-1).UtcTicks)
            {
                start = now.UtcTicks;
                count = 0;
            }
        }

        if (count >= 3)
        {
            return false;
        }

        var value = $"{start.ToString(CultureInfo.InvariantCulture)}|{(count + 1).ToString(CultureInfo.InvariantCulture)}";
        await _db.Database.ExecuteSqlAsync($"""
            insert into identity.user_tokens (user_id, login_provider, name, value)
            values ({userId}, {Provider}, {Name}, {value})
            on conflict (user_id, login_provider, name) do update set value = excluded.value
            """, ct);
        return true;
    }

    private async Task<bool> VerifyConfirmationProofAsync(DuoraUser user, string proof)
    {
        var stored = await _users.GetAuthenticationTokenAsync(user, ConfirmationCodeProvider, ConfirmationCodeName);
        if (stored is not null)
        {
            // Um cadastro por código nunca aceita o token legado como alternativa.
            var parts = stored.Split('|', 3);
            return parts.Length == 3
                && parts[0] == user.SecurityStamp
                && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var issuedTicks)
                && issuedTicks <= _time.GetUtcNow().UtcTicks
                && issuedTicks > _time.GetUtcNow().Subtract(ConfirmationCodeLifetime).UtcTicks
                && proof.Length == 8
                && proof.All(char.IsAsciiDigit)
                && _users.PasswordHasher.VerifyHashedPassword(user, parts[2], proof) != PasswordVerificationResult.Failed;
        }

        // Leitor tolerante: somente cadastros anteriores, ainda sem código, aceitam o link antigo.
        return TryDecodeLinkToken(proof, out var token)
            && await _users.VerifyUserTokenAsync(user, _users.Options.Tokens.EmailConfirmationTokenProvider,
                UserManager<DuoraUser>.ConfirmEmailTokenPurpose, token);
    }

    private static string NewSecurityStamp() => Convert.ToHexString(RandomNumberGenerator.GetBytes(SecurityStampBytes));

    private bool IsExpiredPendingRegistration(DuoraUser user) =>
        !user.EmailConfirmed && user.CreatedAt + _lifecycle.PendingAccountLifetime <= _time.GetUtcNow();

    /// <summary>
    /// E-mail de conta vai para o endereço gravado, nunca para o digitado: a busca é por e-mail normalizado, e o
    /// que foi digitado pode ser uma variante que só coincide na normalização (TM-67).
    /// </summary>
    private static string StoredAddress(DuoraUser user) =>
        user.Email ?? throw new InvalidOperationException("Conta sem e-mail gravado.");

    private void Enqueue(AccountEmailKind kind, string recipient, Guid userId, string? token, EmailLanguage language)
    {
        var linkToken = token is null ? null : Base64Url.EncodeToString(Encoding.UTF8.GetBytes(token));
        var message = new AccountEmailRequested(kind, recipient, userId, linkToken, EmailLanguageTag.Format(language));
        _db.Add(AccountEmailOutbox.Create(message, _time.GetUtcNow()));
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
public sealed class DecoyPasswordHash(IOptions<PasswordHasherOptions> options)
{
    private readonly Lazy<string> _value = new(() =>
        new PasswordHasher<DuoraUser>(options).HashPassword(null!, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));

    /// <summary>
    /// Verifica a senha contra a isca com o hasher indicado (o mesmo da senha real). O PasswordHasher padrão não
    /// usa o usuário, por isso passar null é seguro.
    /// </summary>
    public void Verify(IPasswordHasher<DuoraUser> hasher, string password) =>
        hasher.VerifyHashedPassword(null!, _value.Value, password);
}
