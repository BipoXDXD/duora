using System.Diagnostics;
using Duora.Core.Identity;
using Duora.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Duora.Web.Identity;

/// <summary>
/// Conta que provou a credencial e pode abrir sessão: a política sai dos papéis, e
/// <paramref name="SecondFactorVerified"/> diz se o segundo fator foi provado neste login.
/// </summary>
public sealed record VerifiedAccount(Guid UserId, string SecurityStamp, SessionPolicy Policy, bool SecondFactorVerified);

/// <summary>Resultado da verificação de credenciais. Os casos negativos não dizem se a conta existe.</summary>
public abstract record LoginOutcome
{
    private LoginOutcome()
    {
    }

    public sealed record Verified(VerifiedAccount Account) : LoginOutcome;

    /// <summary>Conta inexistente, senha errada, e-mail não confirmado ou bloqueio temporário: a mesma resposta.</summary>
    public sealed record InvalidCredentials : LoginOutcome;

    /// <summary>Senha correta numa conta com MFA, sem segundo fator na requisição (ADR-0017).</summary>
    public sealed record MfaRequired : LoginOutcome;
}

/// <summary>Resultado da reautenticação (API-A13).</summary>
public abstract record ReauthenticationOutcome
{
    private ReauthenticationOutcome()
    {
    }

    /// <summary>Credencial provada; <paramref name="WithSecondFactor"/> diz se o TOTP também foi.</summary>
    public sealed record Verified(bool WithSecondFactor) : ReauthenticationOutcome;

    public sealed record Invalid : ReauthenticationOutcome;
}

/// <summary>Tempos do login com falha (seção <c>Identity</c>).</summary>
internal sealed class LoginTimingOptions
{
    public const string Section = "Identity";

    /// <summary>
    /// Duração mínima de um login que falha, mais um sorteio de até <see cref="FailedLoginJitter"/>. Fica acima
    /// do custo de um hash, para encobrir o que os caminhos de falha ainda fazem de diferente (a senha errada numa
    /// conta existente grava a falha para o bloqueio; os outros casos não gravam) (TM-06).
    /// </summary>
    public TimeSpan FailedLoginMinimumDuration { get; set; } = TimeSpan.FromMilliseconds(350);

    public TimeSpan FailedLoginJitter { get; set; } = TimeSpan.FromMilliseconds(100);

    public bool IsValid() => FailedLoginMinimumDuration >= TimeSpan.Zero && FailedLoginJitter >= TimeSpan.Zero;
}

/// <summary>
/// Login (API-A04) sobre o <see cref="SignInManager{TUser}"/>. Toda falha custa um hash e não responde antes do
/// piso de latência (TM-06): conta inexistente, e-mail não confirmado e conta bloqueada verificam um hash de isca,
/// porque nesses casos o SignInManager decide antes de calcular o hash.
/// </summary>
public sealed class CredentialVerifier
{
    private readonly UserManager<DuoraUser> _users;
    private readonly SignInManager<DuoraUser> _signIn;
    private readonly DecoyPasswordHash _decoy;
    private readonly LoginTimingOptions _timing;
    private readonly AccountSecurityService _security;

    internal CredentialVerifier(
        UserManager<DuoraUser> users,
        SignInManager<DuoraUser> signIn,
        DecoyPasswordHash decoy,
        IOptions<LoginTimingOptions> timing,
        AccountSecurityService security)
    {
        _users = users;
        _signIn = signIn;
        _decoy = decoy;
        _timing = timing.Value;
        _security = security;
    }

    /// <summary>
    /// Login: senha e, se a conta tem MFA ativo, o segundo fator na mesma requisição. Segundo fator errado responde
    /// como senha errada e conta no mesmo bloqueio (TM-01); o bloqueio só zera depois do segundo fator certo.
    /// </summary>
    public async Task<LoginOutcome> VerifyAsync(
        EmailAddress email,
        string password,
        SecondFactorProof? secondFactor,
        CancellationToken cancellationToken)
    {
        var startedAt = Stopwatch.GetTimestamp();
        var outcome = await CheckAsync(email, password, secondFactor, cancellationToken);
        if (outcome is LoginOutcome.InvalidCredentials)
        {
            await WaitForFailureFloorAsync(startedAt, cancellationToken);
        }

        return outcome;
    }

    /// <summary>
    /// Reautenticação da sessão corrente (API-A13): senha e, com MFA ativo, o código do autenticador. Falhas contam
    /// no bloqueio da conta, como no login.
    /// </summary>
    public async Task<ReauthenticationOutcome> ReauthenticateAsync(
        Guid userId,
        string password,
        string? authenticatorCode,
        CancellationToken cancellationToken)
    {
        var user = await _users.FindByIdAsync(userId.ToString("D"));
        if (user is null || !(await _signIn.CheckPasswordSignInAsync(user, password, lockoutOnFailure: true)).Succeeded)
        {
            return new ReauthenticationOutcome.Invalid();
        }

        if (!user.TwoFactorEnabled)
        {
            return new ReauthenticationOutcome.Verified(WithSecondFactor: false);
        }

        if (authenticatorCode is null
            || !await VerifySecondFactorAsync(user, new SecondFactorProof.AuthenticatorCode(authenticatorCode), cancellationToken))
        {
            return new ReauthenticationOutcome.Invalid();
        }

        return new ReauthenticationOutcome.Verified(WithSecondFactor: true);
    }

    private async Task<LoginOutcome> CheckAsync(
        EmailAddress email,
        string password,
        SecondFactorProof? secondFactor,
        CancellationToken cancellationToken)
    {
        var user = await _users.FindByEmailAsync(email.Value);
        if (user is null)
        {
            _decoy.Verify(_users.PasswordHasher, password);
            return new LoginOutcome.InvalidCredentials();
        }

        var result = await _signIn.CheckPasswordSignInAsync(user, password, lockoutOnFailure: true);
        if (result.IsNotAllowed || result.IsLockedOut)
        {
            _decoy.Verify(_users.PasswordHasher, password);
            return new LoginOutcome.InvalidCredentials();
        }

        if (!result.Succeeded)
        {
            return new LoginOutcome.InvalidCredentials();
        }

        if (user.TwoFactorEnabled)
        {
            if (secondFactor is null)
            {
                return new LoginOutcome.MfaRequired();
            }

            if (!await VerifySecondFactorAsync(user, secondFactor, cancellationToken))
            {
                return new LoginOutcome.InvalidCredentials();
            }
        }

        var policy = AccountRoles.SessionPolicyFor(await _users.GetRolesAsync(user));
        return new LoginOutcome.Verified(new VerifiedAccount(
            user.Id,
            await _users.GetSecurityStampAsync(user),
            policy,
            SecondFactorVerified: user.TwoFactorEnabled));
    }

    /// <summary>Confere o segundo fator; o erro soma no bloqueio e o acerto zera as falhas da senha e do código.</summary>
    private async Task<bool> VerifySecondFactorAsync(DuoraUser user, SecondFactorProof proof, CancellationToken cancellationToken)
    {
        if (!await _security.VerifySecondFactorAsync(user, proof, cancellationToken))
        {
            await _users.AccessFailedAsync(user);
            return false;
        }

        await _users.ResetAccessFailedCountAsync(user);
        return true;
    }

    /// <summary>
    /// Espera em tempo real, não no <see cref="TimeProvider"/> da aplicação: o piso protege contra quem mede a
    /// resposta de fora, e um relógio falso em teste não pode congelá-lo.
    /// </summary>
    private async Task WaitForFailureFloorAsync(long startedAt, CancellationToken cancellationToken)
    {
        var floor = _timing.FailedLoginMinimumDuration + (_timing.FailedLoginJitter * Random.Shared.NextDouble());
        var remaining = floor - Stopwatch.GetElapsedTime(startedAt);
        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining, cancellationToken);
        }
    }
}
