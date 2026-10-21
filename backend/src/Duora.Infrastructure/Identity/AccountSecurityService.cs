using Duora.Core.Identity;
using Duora.Core.Notifications;
using Duora.Infrastructure.Audit;
using Duora.Infrastructure.Email;
using Duora.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Duora.Infrastructure.Identity;

/// <summary>Tetos da espera pela conta (seção <c>Identity</c>): a fila em memória e o lock no PostgreSQL.</summary>
internal sealed class AccountLockOptions
{
    public const string Section = "Identity";

    private static readonly TimeSpan Minimum = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan Maximum = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Teto do lock PostgreSQL (5 s). Não controla a fila em memória.
    /// </summary>
    public TimeSpan AccountLockTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Teto da espera em memória, sem conexão (30 s, hipótese sujeita a medição).</summary>
    public TimeSpan AccountQueueTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public bool IsValid() => AccountLockTimeout >= Minimum && AccountLockTimeout <= Maximum
        && AccountQueueTimeout >= Minimum && AccountQueueTimeout <= Maximum;
}

/// <summary>
/// A conta está ocupada por outra verificação além de um teto: a fila em memória
/// (<see cref="AccountLockOptions.AccountQueueTimeout"/>), o lock no PostgreSQL
/// (<see cref="AccountLockOptions.AccountLockTimeout"/>, <c>lock_timeout</c>, 55P03) ou o prazo total do login
/// (<c>Identity:LoginTimeout</c>). A verificação que não entrou não gravou nada; o cliente pode tentar de novo depois
/// de <see cref="RetryAfter"/>, que é o teto que estourou.
/// </summary>
public sealed class AccountBusyException : Exception
{
    public AccountBusyException()
        : this(TimeSpan.FromSeconds(1))
    {
    }

    public AccountBusyException(string message)
        : base(message)
    {
        RetryAfter = TimeSpan.FromSeconds(1);
    }

    public AccountBusyException(string message, Exception innerException)
        : base(message, innerException)
    {
        RetryAfter = TimeSpan.FromSeconds(1);
    }

    public AccountBusyException(TimeSpan retryAfter, Exception? innerException = null)
        : base("Conta ocupada por outra verificação.", innerException)
    {
        RetryAfter = retryAfter;
    }

    public TimeSpan RetryAfter { get; }
}

/// <summary>Início do cadastro do TOTP (API-A11).</summary>
public abstract record MfaEnrollmentStart
{
    private MfaEnrollmentStart()
    {
    }

    /// <summary>Chave base32 e URI <c>otpauth://</c>, mostradas uma vez; o MFA continua inativo.</summary>
    public sealed record Started(string SharedKey, string AuthenticatorUri) : MfaEnrollmentStart;

    public sealed record AlreadyEnabled : MfaEnrollmentStart;
}

/// <summary>Ativação do TOTP por prova de posse (API-A12).</summary>
public abstract record MfaActivation
{
    private MfaActivation()
    {
    }

    /// <summary>MFA ativo; o security stamp mudou e as sessões da conta foram revogadas.</summary>
    public sealed record Activated(IReadOnlyList<string> RecoveryCodes, string SecurityStamp) : MfaActivation;

    public sealed record InvalidCode : MfaActivation;

    public sealed record NotStarted : MfaActivation;

    public sealed record AlreadyEnabled : MfaActivation;

    /// <summary>A conta ainda usa a senha temporária do bootstrap: primeiro troca a senha, depois ativa o MFA.</summary>
    public sealed record PasswordChangeRequired : MfaActivation;
}

/// <summary>Novo conjunto de códigos de recuperação (API-A14).</summary>
public abstract record RecoveryCodesRegeneration
{
    private RecoveryCodesRegeneration()
    {
    }

    public sealed record Regenerated(IReadOnlyList<string> RecoveryCodes) : RecoveryCodesRegeneration;

    public sealed record MfaNotEnabled : RecoveryCodesRegeneration;
}

/// <summary>Desativação do MFA (API-A15).</summary>
public abstract record MfaDeactivation
{
    private MfaDeactivation()
    {
    }

    /// <summary>MFA desativado; o security stamp mudou e as sessões da conta foram revogadas.</summary>
    public sealed record Disabled(string SecurityStamp) : MfaDeactivation;

    /// <summary>Nada a desativar: o estado final pedido já vale.</summary>
    public sealed record AlreadyDisabled : MfaDeactivation;

    /// <summary>Conta privilegiada: o MFA é o único método exigido e não sai (ADR-0017).</summary>
    public sealed record RequiredByRole : MfaDeactivation;
}

/// <summary>Troca de senha pelo titular (API-A16).</summary>
public abstract record PasswordChange
{
    private PasswordChange()
    {
    }

    /// <summary>Senha trocada; o security stamp mudou e as sessões da conta foram revogadas.</summary>
    public sealed record Changed(string SecurityStamp) : PasswordChange;

    /// <summary>Senha atual errada ou conta bloqueada, sem distinguir.</summary>
    public sealed record WrongPassword : PasswordChange;

    /// <summary>A senha nova repete a temporária do bootstrap (D4), que precisa ser trocada de fato.</summary>
    public sealed record TemporaryPasswordReused : PasswordChange;
}

/// <summary>Prova de segundo fator enviada no login ou na reautenticação.</summary>
public abstract record SecondFactorProof
{
    private SecondFactorProof()
    {
    }

    public sealed record AuthenticatorCode(string Code) : SecondFactorProof;

    public sealed record RecoveryCode(string Code) : SecondFactorProof;
}

/// <summary>Onde a senha de uma conta confirmada foi conferida, para a auditoria (TM-05).</summary>
public enum PasswordCheck
{
    /// <summary>API-A04.</summary>
    Login,

    /// <summary>API-A13.</summary>
    Reauthentication,

    /// <summary>API-A16 (senha atual).</summary>
    PasswordChange,
}

/// <summary>Que segundo fator uma sessão provou: o código de recuperação não vale como TOTP recente.</summary>
public enum SecondFactorKind
{
    None,
    Authenticator,
    RecoveryCode,
}

/// <summary>
/// Verificação de credencial com a conta travada (<c>SELECT ... FOR NO KEY UPDATE</c> na linha da conta), para o
/// contador de falhas e o último passo TOTP não perderem atualização sob requisições paralelas.
/// Quem abre confirma com <see cref="CommitAsync"/>, inclusive quando a verificação falha, para a falha contar.
/// </summary>
public sealed class AccountCheck : IAsyncDisposable
{
    private readonly IDbContextTransaction _transaction;
    private readonly IAsyncDisposable _turn;

    internal AccountCheck(IDbContextTransaction transaction, IAsyncDisposable turn)
    {
        _transaction = transaction;
        _turn = turn;
    }

    public Task CommitAsync(CancellationToken cancellationToken) => _transaction.CommitAsync(cancellationToken);

    /// <summary>Encerra a transação (desfaz se não houve commit) e só então libera a vez da conta na fila.</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await _transaction.DisposeAsync();
        }
        finally
        {
            await _turn.DisposeAsync();
        }
    }
}

/// <summary>
/// Segurança da conta pelo titular: ciclo de vida do TOTP, códigos de recuperação, reautenticação e troca de senha
/// (API-A11 a A16; ADR-0017). Cada mudança roda numa transação com o registro de auditoria (TM-05). A chave do
/// autenticador passa pelo <see cref="UserManager{TUser}"/> do host, que a protege em repouso (decisão D2), e os
/// códigos, pelo <see cref="DuoraUserStore"/>, que guarda só o hash (D3). Nenhum segredo vai a log.
/// </summary>
public sealed class AccountSecurityService
{
    /// <summary>Tamanho de cada conjunto de códigos de recuperação (hipótese, auth.md).</summary>
    public const int RecoveryCodeCount = 10;

    private const string Issuer = "Duora";
    private const string AuditTargetType = "user";
    private const string MfaChangeReason = "mfa_change";
    private const string PasswordChangeReason = "password_change";

    /// <summary>Padrão do PostgreSQL: sem teto para esperar lock; o timeout do comando continua valendo.</summary>
    private const string NoLockTimeout = "0";

    // Os mesmos motivos que a revogação pela chave (ticket store) e a de A10 já gravavam.
    private const string LogoutReason = "logout";
    private const string RevokedByOwnerReason = "revoked-by-owner";

    private readonly DuoraDbContext _db;
    private readonly UserManager<DuoraUser> _users;
    private readonly AuditTrail _audit;
    private readonly TimeProvider _time;
    private readonly AccountLockOptions _lock;
    private readonly AccountCheckGate _gate;

    internal AccountSecurityService(
        DuoraDbContext db,
        UserManager<DuoraUser> users,
        TimeProvider time,
        AccountLockOptions accountLock,
        AccountCheckGate gate)
    {
        _gate = gate;
        _db = db;
        _users = users;
        _audit = new AuditTrail(db, time);
        _time = time;
        _lock = accountLock;
    }

    /// <summary>
    /// Serializa logins antes de procurar a conta, inclusive endereços inexistentes. A chave usa a mesma
    /// normalização do Identity, não é persistida nem registrada e sai da memória com o último interessado.
    /// Sempre adquirir antes da vez por ID: chamar com a vez da conta aberta neste fluxo falha alto.
    /// </summary>
    /// <exception cref="InvalidOperationException">Este fluxo já tem a vez de uma conta (ordem invertida).</exception>
    public Task<IAsyncDisposable> EnterLoginAsync(EmailAddress email, CancellationToken cancellationToken)
    {
        _gate.ThrowIfAccountTurnHeld();
        var normalized = _users.NormalizeEmail(email.Value)
            ?? throw new InvalidOperationException("Normalizador de e-mail retornou null.");
        return _gate.EnterAsync($"login:{normalized}", _lock.AccountQueueTimeout, cancellationToken);
    }

    /// <summary>
    /// Abre a verificação de credencial da conta: transação com a linha da conta travada e a entidade relida, para
    /// o bloqueio do Identity (que lê, soma e grava) não perder falhas sob requisições paralelas.
    /// </summary>
    /// <remarks>
    /// Não é <c>async</c>: a vez da conta é pedida no contexto de quem chama, para a marca de vez aberta
    /// (<see cref="AccountCheckGate"/>) valer até o <see cref="AccountCheck"/> ser descartado.
    /// </remarks>
    public Task<AccountCheck> LockAccountAsync(DuoraUser user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        var enteringTurn = _gate.EnterAsync(user.Id, _lock.AccountQueueTimeout, cancellationToken);
        return OpenCheckAsync(user, enteringTurn, cancellationToken);
    }

    private async Task<AccountCheck> OpenCheckAsync(
        DuoraUser user,
        Task<IAsyncDisposable> enteringTurn,
        CancellationToken cancellationToken)
    {
        var turn = await enteringTurn;
        try
        {
            var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                await LockAndReloadAsync(user, cancellationToken);
            }
            catch
            {
                await transaction.DisposeAsync();
                throw;
            }

            return new AccountCheck(transaction, turn);
        }
        catch
        {
            await turn.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Gera uma chave nova (descartando um cadastro anterior não confirmado). Trocar a chave troca o security stamp,
    /// como o Identity faz; a sessão que pediu o cadastro adota o stamp novo e as outras caem.
    /// </summary>
    public async Task<MfaEnrollmentStart> StartMfaEnrollmentAsync(
        Guid userId,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        await using var turn = await _gate.EnterAsync(userId, _lock.AccountQueueTimeout, cancellationToken);
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        var user = await LockUserAsync(userId, cancellationToken);
        if (user.TwoFactorEnabled)
        {
            return new MfaEnrollmentStart.AlreadyEnabled();
        }

        EnsureSucceeded(await _users.ResetAuthenticatorKeyAsync(user), "trocar a chave do autenticador");
        var sharedKey = await _users.GetAuthenticatorKeyAsync(user)
            ?? throw new InvalidOperationException("Chave do autenticador ilegível logo depois de gravada.");
        await AdoptSecurityStampAsync(user, sessionId, cancellationToken);
        await AppendAuditAsync(user.Id, AuditAction.MfaEnrollmentStarted, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new MfaEnrollmentStart.Started(sharedKey, AuthenticatorUri(user.Email ?? "", sharedKey));
    }

    /// <summary>
    /// Ativa o TOTP com a prova de posse. Com a conta travada, uma segunda ativação simultânea vê o MFA já ativo
    /// (409) em vez de disputar o concurrency stamp; o passo do código fica registrado e não serve para login.
    /// O titular recebe um aviso em <paramref name="language"/> (TM-21).
    /// </summary>
    public async Task<MfaActivation> ConfirmMfaEnrollmentAsync(
        Guid userId,
        string code,
        EmailLanguage language,
        CancellationToken cancellationToken)
    {
        await using var turn = await _gate.EnterAsync(userId, _lock.AccountQueueTimeout, cancellationToken);
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        var user = await LockUserAsync(userId, cancellationToken);
        if (user.HasTemporaryPassword)
        {
            // A rota já barra essa sessão; o serviço não depende disso para manter a ordem troca de senha, depois MFA.
            return new MfaActivation.PasswordChangeRequired();
        }

        if (user.TwoFactorEnabled)
        {
            return new MfaActivation.AlreadyEnabled();
        }

        if (await _users.GetAuthenticatorKeyAsync(user) is null)
        {
            return new MfaActivation.NotStarted();
        }

        if (!await TryAcceptAuthenticatorCodeAsync(user, code, cancellationToken))
        {
            return new MfaActivation.InvalidCode();
        }

        EnsureSucceeded(await _users.SetTwoFactorEnabledAsync(user, enabled: true), "ativar o MFA");
        var codes = await GenerateRecoveryCodesAsync(user);
        await RevokeAllSessionsAsync(user.Id, MfaChangeReason, cancellationToken);
        await AppendAuditAsync(user.Id, AuditAction.MfaEnabled, cancellationToken);
        await NotifyHolderAsync(user, SecurityChange.MfaEnabled, language, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new MfaActivation.Activated(codes, user.SecurityStamp ?? "");
    }

    /// <summary>Troca o conjunto inteiro: os códigos anteriores deixam de valer no mesmo commit, e o titular é avisado.</summary>
    public async Task<RecoveryCodesRegeneration> RegenerateRecoveryCodesAsync(
        Guid userId,
        EmailLanguage language,
        CancellationToken cancellationToken)
    {
        await using var turn = await _gate.EnterAsync(userId, _lock.AccountQueueTimeout, cancellationToken);
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        var user = await LockUserAsync(userId, cancellationToken);
        if (!user.TwoFactorEnabled)
        {
            return new RecoveryCodesRegeneration.MfaNotEnabled();
        }

        var codes = await GenerateRecoveryCodesAsync(user);
        await AppendAuditAsync(user.Id, AuditAction.MfaRecoveryCodesRegenerated, cancellationToken);
        await NotifyHolderAsync(user, SecurityChange.RecoveryCodesRegenerated, language, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new RecoveryCodesRegeneration.Regenerated(codes);
    }

    /// <summary>Desativa o TOTP de participante e avisa o titular; MFA já inativo não muda nada nem avisa.</summary>
    public async Task<MfaDeactivation> DisableMfaAsync(Guid userId, EmailLanguage language, CancellationToken cancellationToken)
    {
        await using var turn = await _gate.EnterAsync(userId, _lock.AccountQueueTimeout, cancellationToken);
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        var user = await LockUserAsync(userId, cancellationToken);
        if ((await _users.GetRolesAsync(user)).Any(AccountRoles.IsPrivileged))
        {
            return new MfaDeactivation.RequiredByRole();
        }

        if (!user.TwoFactorEnabled)
        {
            return new MfaDeactivation.AlreadyDisabled();
        }

        EnsureSucceeded(await _users.SetTwoFactorEnabledAsync(user, enabled: false), "desativar o MFA");
        // A chave antiga e os códigos não podem reativar nada: a chave é trocada por uma que ninguém viu e o
        // conjunto de códigos fica vazio.
        EnsureSucceeded(await _users.ResetAuthenticatorKeyAsync(user), "descartar a chave do autenticador");
        await _users.GenerateNewTwoFactorRecoveryCodesAsync(user, number: 0);
        await RevokeAllSessionsAsync(user.Id, MfaChangeReason, cancellationToken);
        await AppendAuditAsync(user.Id, AuditAction.MfaDisabled, cancellationToken);
        await NotifyHolderAsync(user, SecurityChange.MfaDisabled, language, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new MfaDeactivation.Disabled(user.SecurityStamp ?? "");
    }

    /// <summary>
    /// Confere o segundo fator de uma conta com MFA ativo, dentro de uma <see cref="AccountCheck"/> já aberta. O
    /// acerto zera as falhas; o erro soma no bloqueio da conta e é auditado (a senha estava certa). TOTP só vale
    /// uma vez: o passo aceito precisa ser maior que o último. Código de recuperação é de uso único.
    /// </summary>
    /// <returns>O fator provado, ou <see cref="SecondFactorKind.None"/> quando a prova falhou.</returns>
    public async Task<SecondFactorKind> VerifySecondFactorAsync(
        DuoraUser user,
        SecondFactorProof proof,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        RequireAccountCheck();
        var proved = proof switch
        {
            SecondFactorProof.AuthenticatorCode(var code) =>
                await TryAcceptAuthenticatorCodeAsync(user, code, cancellationToken)
                    ? SecondFactorKind.Authenticator
                    : SecondFactorKind.None,
            SecondFactorProof.RecoveryCode(var code) =>
                (await _users.RedeemTwoFactorRecoveryCodeAsync(user, code)).Succeeded
                    ? SecondFactorKind.RecoveryCode
                    : SecondFactorKind.None,
            _ => throw new ArgumentOutOfRangeException(nameof(proof), proof, "Prova de segundo fator desconhecida."),
        };

        if (proved == SecondFactorKind.None)
        {
            // A senha acabou de conferir, então a conta não estava bloqueada antes desta falha.
            EnsureSucceeded(await _users.AccessFailedAsync(user), "contar a falha do segundo fator");
            await AppendAuditAsync(user.Id, AuditAction.SecondFactorFailed, cancellationToken);
            await RecordLockoutIfStartedAsync(user, cancellationToken);
            return SecondFactorKind.None;
        }

        EnsureSucceeded(await _users.ResetAccessFailedCountAsync(user), "zerar as falhas de login");
        if (proved == SecondFactorKind.RecoveryCode)
        {
            await AppendAuditAsync(user.Id, AuditAction.MfaRecoveryCodeUsed, cancellationToken);
            await NotifyHolderAsync(user, SecurityChange.RecoveryCodeUsed,
                EmailLanguageTag.Match(user.PreferredLocale) ?? EmailLanguageTag.Default, cancellationToken);
        }

        return proved;
    }

    /// <summary>
    /// Registra na auditoria o login que abre sessão, dentro da <see cref="AccountCheck"/> que o verificou: o registro
    /// confirma junto com o contador de falhas.
    /// </summary>
    public Task RecordLoginSucceededAsync(DuoraUser user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        RequireAccountCheck();
        return AppendAuditAsync(user.Id, AuditAction.LoginSucceeded, cancellationToken);
    }

    /// <summary>
    /// Registra a senha certa de uma conta com MFA num pedido sem o segundo fator (A04 ou A13), dentro da
    /// <see cref="AccountCheck"/>: nada foi aberto, mas a senha conferiu, o que importa se ela tiver vazado.
    /// </summary>
    public Task RecordSecondFactorRequiredAsync(DuoraUser user, PasswordCheck check, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        RequireAccountCheck();
        var action = check switch
        {
            PasswordCheck.Login => AuditAction.LoginSecondFactorRequired,
            PasswordCheck.Reauthentication => AuditAction.ReauthenticationSecondFactorRequired,
            _ => throw new ArgumentOutOfRangeException(nameof(check), check, "Só login e reautenticação pedem o segundo fator."),
        };
        return AppendAuditAsync(user.Id, action, cancellationToken);
    }

    /// <summary>
    /// Registra a senha que não abriu a conta, na transação que travou a conta e contou a falha. Se a conta já estava
    /// bloqueada antes, a senha nem foi conferida, e o registro diz isso; senão, a falha é registrada e, se ela levou
    /// a conta ao teto, o início do bloqueio também (uma vez, não a cada tentativa seguinte).
    /// </summary>
    public async Task RecordPasswordFailureAsync(
        DuoraUser user,
        PasswordCheck check,
        bool wasLockedOut,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        RequireAccountCheck();
        var (failed, refusedLockedOut) = check switch
        {
            PasswordCheck.Login => (AuditAction.LoginFailed, AuditAction.LoginRefusedLockedOut),
            PasswordCheck.Reauthentication =>
                (AuditAction.ReauthenticationFailed, AuditAction.ReauthenticationRefusedLockedOut),
            PasswordCheck.PasswordChange => (AuditAction.PasswordChangeFailed, AuditAction.PasswordChangeRefusedLockedOut),
            _ => throw new ArgumentOutOfRangeException(nameof(check), check, "Verificação de senha desconhecida."),
        };
        if (wasLockedOut)
        {
            await AppendAuditAsync(user.Id, refusedLockedOut, cancellationToken);
            return;
        }

        await AppendAuditAsync(user.Id, failed, cancellationToken);
        await RecordLockoutIfStartedAsync(user, cancellationToken);
    }

    /// <summary>
    /// Encerra uma sessão do próprio titular (API-A10) e registra a revogação, com a sessão como alvo. Falso quando a
    /// sessão não existe ou é de outra pessoa, casos que a API não distingue (404). Revogar de novo é sucesso, sem
    /// novo registro.
    /// </summary>
    public Task<bool> RevokeOwnSessionAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken) =>
        RevokeAndAuditAsync(userId, sessionId, RevokedByOwnerReason, AuditAction.SessionRevoked, cancellationToken);

    /// <summary>
    /// Registra o logout (API-A05) da sessão corrente, revogando-a na mesma transação. O host ainda apaga o cookie e
    /// revoga pela chave, o que não muda uma sessão já revogada.
    /// </summary>
    public Task RecordLogoutAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken) =>
        RevokeAndAuditAsync(userId, sessionId, LogoutReason, AuditAction.LoggedOut, cancellationToken);

    /// <summary>
    /// Registra uma reautenticação (API-A13) na sessão corrente. Com TOTP, conta também como prova recente de TOTP,
    /// a que o step-up administrativo e o A14 exigem.
    /// </summary>
    public async Task RecordReauthenticationAsync(
        Guid userId,
        Guid sessionId,
        bool withAuthenticator,
        CancellationToken cancellationToken)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        var now = _time.GetUtcNow();
        var updated = await _db.Database.ExecuteSqlAsync(
            $"""
            update identity.login_sessions
            set reauthenticated_at = {now},
                mfa_verified_at = case when {withAuthenticator} then {now} else mfa_verified_at end,
                totp_verified_at = case when {withAuthenticator} then {now} else totp_verified_at end
            where id = {sessionId} and user_id = {userId} and revoked_at is null
            """,
            cancellationToken);
        if (updated != 1)
        {
            throw new InvalidOperationException("Sessão corrente não encontrada ao registrar a reautenticação.");
        }

        await AppendAuditAsync(userId, AuditAction.Reauthenticated, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Troca a senha conferindo a atual. Senha errada conta para o bloqueio da conta, como no login: quem tem só a
    /// sessão não pode testar senhas à vontade. A troca avisa o titular; a senha errada, não. Trocar a senha
    /// temporária do bootstrap (D4) encerra essa fase: a marca sai no mesmo commit, e a sessão seguinte já é a
    /// restrita do cadastro de MFA.
    /// </summary>
    public async Task<PasswordChange> ChangePasswordAsync(
        Guid userId,
        string currentPassword,
        string newPassword,
        EmailLanguage language,
        CancellationToken cancellationToken)
    {
        await using var turn = await _gate.EnterAsync(userId, _lock.AccountQueueTimeout, cancellationToken);
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        var user = await LockUserAsync(userId, cancellationToken);
        if (await _users.IsLockedOutAsync(user))
        {
            await RecordPasswordFailureAsync(user, PasswordCheck.PasswordChange, wasLockedOut: true, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new PasswordChange.WrongPassword();
        }

        if (user.HasExpiredTemporaryPassword(_time.GetUtcNow()))
        {
            // Sob o lock: a sessão pode ter sido aberta antes do prazo, e a senha temporária vencida não vale mais.
            await RecordPasswordFailureAsync(user, PasswordCheck.PasswordChange, wasLockedOut: false, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new PasswordChange.WrongPassword();
        }

        var current = user.PasswordToVerify(currentPassword);
        if (KeepsTemporaryPassword(user, current, newPassword))
        {
            return new PasswordChange.TemporaryPasswordReused();
        }

        var result = await _users.ChangePasswordAsync(user, current, newPassword);
        if (result.Errors.Any(error => error.Code == nameof(IdentityErrorDescriber.PasswordMismatch)))
        {
            EnsureSucceeded(await _users.AccessFailedAsync(user), "contar a falha de senha");
            await RecordPasswordFailureAsync(user, PasswordCheck.PasswordChange, wasLockedOut: false, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new PasswordChange.WrongPassword();
        }

        EnsureSucceeded(result, "trocar a senha");
        await _db.Database.ExecuteSqlAsync(
            $"update identity.users set temporary_password_expires_at = null where id = {user.Id}",
            cancellationToken);
        await RevokeAllSessionsAsync(user.Id, PasswordChangeReason, cancellationToken);
        await AppendAuditAsync(user.Id, AuditAction.PasswordChanged, cancellationToken);
        await NotifyHolderAsync(user, SecurityChange.PasswordChanged, language, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new PasswordChange.Changed(user.SecurityStamp ?? "");
    }

    /// <summary>
    /// A senha nova repete a temporária do bootstrap? Só responde depois de conferir a senha atual: com ela errada, a
    /// troca segue e falha como senha errada, contando no bloqueio, sem dizer nada sobre a nova.
    /// </summary>
    private bool KeepsTemporaryPassword(DuoraUser user, string current, string newPassword) =>
        user.HasTemporaryPassword
        && string.Equals(TemporaryPassword.Normalize(newPassword), current, StringComparison.Ordinal)
        && user.PasswordHash is { } hash
        && _users.PasswordHasher.VerifyHashedPassword(user, hash, current) != PasswordVerificationResult.Failed;

    /// <summary>
    /// Aceita o código do autenticador só se o passo dele for posterior ao último aceito: um UPDATE condicional que
    /// grava o passo e só afeta a linha se ele avançar. O mesmo código, ou um anterior ainda na tolerância, falha.
    /// </summary>
    private async Task<bool> TryAcceptAuthenticatorCodeAsync(DuoraUser user, string code, CancellationToken cancellationToken)
    {
        if (await _users.GetAuthenticatorKeyAsync(user) is not { } sharedKey)
        {
            return false;
        }

        byte[] key;
        try
        {
            key = Totp.DecodeBase32Key(sharedKey);
        }
        catch (FormatException)
        {
            // Chave gravada fora do formato: falha fechada, como chave ausente.
            return false;
        }

        if (Totp.MatchStep(key, Totp.NormalizeInput(code), _time.GetUtcNow()) is not { } step)
        {
            return false;
        }

        var accepted = await _db.Database.ExecuteSqlAsync(
            $"""
            update identity.users set last_totp_step = {step}
            where id = {user.Id} and (last_totp_step is null or last_totp_step < {step})
            """,
            cancellationToken);
        // A entidade rastreada precisa ver o passo novo: o próximo UpdateAsync do Identity grava a linha inteira.
        await _db.Entry(user).ReloadAsync(cancellationToken);
        return accepted == 1;
    }

    private async Task<IReadOnlyList<string>> GenerateRecoveryCodesAsync(DuoraUser user) =>
        [.. await _users.GenerateNewTwoFactorRecoveryCodesAsync(user, RecoveryCodeCount)
            ?? throw new InvalidOperationException("O Identity não gravou os códigos de recuperação.")];

    private async Task<DuoraUser> LockUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await _users.FindByIdAsync(userId.ToString("D"))
            ?? throw new InvalidOperationException("Conta da sessão não existe mais.");
        await LockAndReloadAsync(user, cancellationToken);
        return user;
    }

    /// <summary>
    /// Trava a linha da conta até o fim da transação e relê a entidade, que pode ter sido carregada antes do lock.
    /// <c>FOR NO KEY UPDATE</c> serializa as verificações da conta sem bloquear quem só referencia a conta por FK
    /// (abrir sessão, gravar aceite). A espera tem teto (<c>lock_timeout</c> só desta transação): sem ele, uma fila
    /// de verificações da mesma conta seguraria conexões do pool até o timeout do comando.
    /// </summary>
    /// <exception cref="AccountBusyException">A espera passou do teto; a transação não gravou nada.</exception>
    private async Task LockAndReloadAsync(DuoraUser user, CancellationToken cancellationToken)
    {
        await WithLockCeilingAsync(
            () => _db.Database.ExecuteSqlAsync(
                $"select 1 from identity.users where id = {user.Id} for no key update",
                cancellationToken),
            cancellationToken);
        await _db.Entry(user).ReloadAsync(cancellationToken);
    }

    /// <summary>
    /// Roda <paramref name="takeLock"/> com <c>lock_timeout</c> no teto da conta. Passou do teto,
    /// <see cref="AccountBusyException"/> (503) em vez de segurar a conexão até o timeout do comando. O teto vale só
    /// para essa espera: o <c>set_config(..., true)</c> duraria a transação inteira, e um comando seguinte que esperasse
    /// outra linha (as sessões, no reset) estouraria no meio da operação, como 500. Depois do lock, volta ao padrão.
    /// </summary>
    private async Task<T> WithLockCeilingAsync<T>(Func<Task<T>> takeLock, CancellationToken cancellationToken)
    {
        await SetLockTimeoutAsync($"{(long)_lock.AccountLockTimeout.TotalMilliseconds}ms", cancellationToken);
        T locked;
        try
        {
            locked = await takeLock();
        }
        catch (Exception error) when (LockTimeoutIn(error) is { } timeout)
        {
            throw new AccountBusyException(_lock.AccountLockTimeout, timeout);
        }

        await SetLockTimeoutAsync(NoLockTimeout, cancellationToken);
        return locked;
    }

    private Task<int> SetLockTimeoutAsync(string lockTimeout, CancellationToken cancellationToken) =>
        _db.Database.ExecuteSqlAsync($"select set_config('lock_timeout', {lockTimeout}, true)", cancellationToken);

    /// <summary>
    /// O 55P03 chega cru de um comando e embrulhado pelo EF (falha transitória) de uma query; os dois são a espera
    /// que passou do teto.
    /// </summary>
    private static PostgresException? LockTimeoutIn(Exception error) =>
        (error as PostgresException ?? error.InnerException as PostgresException) is
        { SqlState: PostgresErrorCodes.LockNotAvailable } timeout
            ? timeout
            : null;

    /// <summary>
    /// Registra o início do bloqueio quando a falha recém-contada levou ao teto uma conta que não estava bloqueada.
    /// </summary>
    private async Task RecordLockoutIfStartedAsync(DuoraUser user, CancellationToken cancellationToken)
    {
        if (await _users.IsLockedOutAsync(user))
        {
            await AppendAuditAsync(user.Id, AuditAction.AccountLockedOut, cancellationToken);
        }
    }

    private void RequireAccountCheck()
    {
        if (_db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("O segundo fator só é conferido com a conta travada (AccountCheck).");
        }
    }

    private async Task AdoptSecurityStampAsync(DuoraUser user, Guid sessionId, CancellationToken cancellationToken)
    {
        var stamp = user.SecurityStamp ?? "";
        await _db.Database.ExecuteSqlAsync(
            $"""
            update identity.login_sessions set security_stamp = {stamp}
            where id = {sessionId} and user_id = {user.Id} and revoked_at is null
            """,
            cancellationToken);
    }

    /// <summary>
    /// O stamp novo já invalida as sessões; a revogação explícita deixa o motivo registrado. A sessão que fez a
    /// mudança é renovada pelo host (nova chave e novo cookie).
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

    /// <summary>
    /// Aviso ao titular na transação da mudança (TM-21): se ela volta atrás, o aviso some junto. Vai para o endereço
    /// gravado; o texto é fixo e não leva nada da requisição.
    /// </summary>
    private async Task NotifyHolderAsync(
        DuoraUser user,
        SecurityChange change,
        EmailLanguage language,
        CancellationToken cancellationToken)
    {
        var to = user.Email ?? throw new InvalidOperationException("Conta sem e-mail gravado.");
        _db.Add(AccountEmailOutbox.CreateSecurityNotice(change, to, user.Id, language, _time.GetUtcNow(), user.PreferredTimeZone));
        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Revoga a sessão da conta e registra o fato, na mesma transação, só se ela ainda estava ativa. A linha é
    /// travada na leitura: dois pedidos simultâneos para a mesma sessão registram uma revogação, não duas.
    /// <c>FOR NO KEY UPDATE</c> basta, porque só colunas que não são chave mudam, e não espera por quem só
    /// referencia a sessão. A espera tem o mesmo teto da conta.
    /// </summary>
    /// <exception cref="AccountBusyException">A espera passou do teto; nada foi gravado.</exception>
    private async Task<bool> RevokeAndAuditAsync(
        Guid userId,
        Guid sessionId,
        string reason,
        AuditAction action,
        CancellationToken cancellationToken)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        var state = await WithLockCeilingAsync(
            () => _db.Database
                .SqlQuery<bool>(
                    $"""
                    select revoked_at is null as "Value" from identity.login_sessions
                    where id = {sessionId} and user_id = {userId}
                    for no key update
                    """)
                .ToListAsync(cancellationToken),
            cancellationToken);
        if (state is not [var active])
        {
            return false;
        }

        if (active)
        {
            var now = _time.GetUtcNow();
            await _db.Database.ExecuteSqlAsync(
                $"""
                update identity.login_sessions set revoked_at = {now}, revocation_reason = {reason}
                where id = {sessionId} and user_id = {userId}
                """,
                cancellationToken);
            await _audit.AppendAsync(
                new AuditRecord(AuditActorKind.User, userId, action, AuditTargetTypes.LoginSession, sessionId),
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private Task AppendAuditAsync(Guid userId, AuditAction action, CancellationToken cancellationToken) =>
        _audit.AppendAsync(new AuditRecord(AuditActorKind.User, userId, action, AuditTargetType, userId), cancellationToken);

    /// <summary>Formato que os autenticadores leem (Key Uri Format): rótulo "emissor:conta", segredo e emissor.</summary>
    private static string AuthenticatorUri(string email, string sharedKey) =>
        $"otpauth://totp/{Uri.EscapeDataString($"{Issuer}:{email}")}?secret={sharedKey}&issuer={Issuer}&digits=6";

    private static void EnsureSucceeded(IdentityResult result, string operation)
    {
        if (!result.Succeeded)
        {
            var codes = string.Join(", ", result.Errors.Select(error => error.Code));
            throw new InvalidOperationException($"O Identity recusou {operation}: {codes}.");
        }
    }
}
