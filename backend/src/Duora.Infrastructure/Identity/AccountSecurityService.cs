using Duora.Core.Identity;
using Duora.Infrastructure.Audit;
using Duora.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Duora.Infrastructure.Identity;

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

    private readonly DuoraDbContext _db;
    private readonly UserManager<DuoraUser> _users;
    private readonly AuditTrail _audit;
    private readonly TimeProvider _time;

    internal AccountSecurityService(DuoraDbContext db, UserManager<DuoraUser> users, TimeProvider time)
    {
        _db = db;
        _users = users;
        _audit = new AuditTrail(db, time);
        _time = time;
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
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        var user = await RequireUserAsync(userId);
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

    public async Task<MfaActivation> ConfirmMfaEnrollmentAsync(Guid userId, string code, CancellationToken cancellationToken)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        var user = await RequireUserAsync(userId);
        if (user.TwoFactorEnabled)
        {
            return new MfaActivation.AlreadyEnabled();
        }

        if (await _users.GetAuthenticatorKeyAsync(user) is null)
        {
            return new MfaActivation.NotStarted();
        }

        if (!await VerifyAuthenticatorCodeAsync(user, code))
        {
            return new MfaActivation.InvalidCode();
        }

        EnsureSucceeded(await _users.SetTwoFactorEnabledAsync(user, enabled: true), "ativar o MFA");
        var codes = await GenerateRecoveryCodesAsync(user);
        await RevokeAllSessionsAsync(user.Id, MfaChangeReason, cancellationToken);
        await AppendAuditAsync(user.Id, AuditAction.MfaEnabled, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new MfaActivation.Activated(codes, user.SecurityStamp ?? "");
    }

    /// <summary>Troca o conjunto inteiro: os códigos anteriores deixam de valer no mesmo commit.</summary>
    public async Task<RecoveryCodesRegeneration> RegenerateRecoveryCodesAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        var user = await RequireUserAsync(userId);
        if (!user.TwoFactorEnabled)
        {
            return new RecoveryCodesRegeneration.MfaNotEnabled();
        }

        var codes = await GenerateRecoveryCodesAsync(user);
        await AppendAuditAsync(user.Id, AuditAction.MfaRecoveryCodesRegenerated, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new RecoveryCodesRegeneration.Regenerated(codes);
    }

    public async Task<MfaDeactivation> DisableMfaAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        var user = await RequireUserAsync(userId);
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
        await transaction.CommitAsync(cancellationToken);
        return new MfaDeactivation.Disabled(user.SecurityStamp ?? "");
    }

    /// <summary>
    /// Confere o segundo fator de uma conta com MFA ativo. Código de recuperação é de uso único e o resgate é
    /// auditado; o TOTP não grava nada.
    /// </summary>
    public async Task<bool> VerifySecondFactorAsync(DuoraUser user, SecondFactorProof proof, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        switch (proof)
        {
            case SecondFactorProof.AuthenticatorCode(var code):
                return await VerifyAuthenticatorCodeAsync(user, code);
            case SecondFactorProof.RecoveryCode(var code):
                await using (var transaction = await _db.Database.BeginTransactionAsync(cancellationToken))
                {
                    if (!(await _users.RedeemTwoFactorRecoveryCodeAsync(user, code)).Succeeded)
                    {
                        return false;
                    }

                    await AppendAuditAsync(user.Id, AuditAction.MfaRecoveryCodeUsed, cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    return true;
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(proof), proof, "Prova de segundo fator desconhecida.");
        }
    }

    /// <summary>
    /// Registra uma reautenticação (API-A13) na sessão corrente. Com segundo fator, conta também como prova recente
    /// de MFA, a que o step-up administrativo exige.
    /// </summary>
    public async Task RecordReauthenticationAsync(
        Guid userId,
        Guid sessionId,
        bool withSecondFactor,
        CancellationToken cancellationToken)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        var now = _time.GetUtcNow();
        var updated = await _db.Database.ExecuteSqlAsync(
            $"""
            update identity.login_sessions
            set reauthenticated_at = {now},
                mfa_verified_at = case when {withSecondFactor} then {now} else mfa_verified_at end
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
    /// sessão não pode testar senhas à vontade.
    /// </summary>
    public async Task<PasswordChange> ChangePasswordAsync(
        Guid userId,
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        var user = await RequireUserAsync(userId);
        if (await _users.IsLockedOutAsync(user))
        {
            return new PasswordChange.WrongPassword();
        }

        var result = await _users.ChangePasswordAsync(user, currentPassword, newPassword);
        if (result.Errors.Any(error => error.Code == nameof(IdentityErrorDescriber.PasswordMismatch)))
        {
            await _users.AccessFailedAsync(user);
            await transaction.CommitAsync(cancellationToken);
            return new PasswordChange.WrongPassword();
        }

        EnsureSucceeded(result, "trocar a senha");
        await RevokeAllSessionsAsync(user.Id, PasswordChangeReason, cancellationToken);
        await AppendAuditAsync(user.Id, AuditAction.PasswordChanged, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new PasswordChange.Changed(user.SecurityStamp ?? "");
    }

    private Task<bool> VerifyAuthenticatorCodeAsync(DuoraUser user, string code) =>
        _users.VerifyTwoFactorTokenAsync(user, _users.Options.Tokens.AuthenticatorTokenProvider, code);

    private async Task<IReadOnlyList<string>> GenerateRecoveryCodesAsync(DuoraUser user) =>
        [.. await _users.GenerateNewTwoFactorRecoveryCodesAsync(user, RecoveryCodeCount)
            ?? throw new InvalidOperationException("O Identity não gravou os códigos de recuperação.")];

    private async Task<DuoraUser> RequireUserAsync(Guid userId) =>
        await _users.FindByIdAsync(userId.ToString("D"))
        ?? throw new InvalidOperationException("Conta da sessão não existe mais.");

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
