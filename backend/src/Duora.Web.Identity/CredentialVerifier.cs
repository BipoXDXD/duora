using System.Diagnostics;
using Duora.Core.Identity;
using Duora.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Duora.Web.Identity;

/// <summary>
/// Conta que provou a credencial e pode abrir sessão: a política sai dos papéis, e
/// <paramref name="SecondFactor"/> diz qual segundo fator foi provado neste login.
/// </summary>
public sealed record VerifiedAccount(Guid UserId, string SecurityStamp, SessionPolicy Policy, SecondFactorKind SecondFactor);

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

    /// <summary>Credencial provada; <paramref name="WithAuthenticator"/> diz se o TOTP também foi.</summary>
    public sealed record Verified(bool WithAuthenticator) : ReauthenticationOutcome;

    public sealed record Invalid : ReauthenticationOutcome;
}

/// <summary>Tempos do login com falha (seção <c>Identity</c>).</summary>
internal sealed class LoginTimingOptions
{
    public const string Section = "Identity";

    private static readonly TimeSpan MaximumLoginTimeout = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Duração mínima de um login que falha, mais um sorteio de até <see cref="FailedLoginJitter"/>. Fica acima
    /// do custo de um hash, para encobrir o que os caminhos de falha ainda fazem de diferente (a senha errada numa
    /// conta existente grava a falha para o bloqueio; os outros casos não gravam) (TM-06).
    /// </summary>
    public TimeSpan FailedLoginMinimumDuration { get; set; } = TimeSpan.FromMilliseconds(350);

    public TimeSpan FailedLoginJitter { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Prazo das esperas do login (filas, lock e piso), 30 s, hipótese; as gravações depois do hash não
    /// cancelam. Estourou, <see cref="AccountBusyException"/> (503) com esse valor no <c>Retry-After</c>. Precisa
    /// caber o piso inteiro, ou toda falha viraria 503.
    /// </summary>
    public TimeSpan LoginTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public bool IsValid() => FailedLoginMinimumDuration >= TimeSpan.Zero && FailedLoginJitter >= TimeSpan.Zero
        && LoginTimeout >= FailedLoginMinimumDuration + FailedLoginJitter && LoginTimeout <= MaximumLoginTimeout;
}

/// <summary>
/// Login (API-A04) sobre o <see cref="SignInManager{TUser}"/>. Toda falha custa um hash e não responde antes do
/// piso de latência (TM-06): conta inexistente, e-mail não confirmado e conta bloqueada verificam um hash de isca,
/// porque nesses casos o SignInManager decide antes de calcular o hash.
/// </summary>
public sealed partial class CredentialVerifier
{
    private readonly UserManager<DuoraUser> _users;
    private readonly SignInManager<DuoraUser> _signIn;
    private readonly DecoyPasswordHash _decoy;
    private readonly LoginTimingOptions _timing;
    private readonly AccountSecurityService _security;
    private readonly LoginMetrics _metrics;
    private readonly ILogger<CredentialVerifier> _logger;
    private readonly TimeProvider _time;

    internal CredentialVerifier(
        UserManager<DuoraUser> users,
        SignInManager<DuoraUser> signIn,
        DecoyPasswordHash decoy,
        IOptions<LoginTimingOptions> timing,
        AccountSecurityService security,
        LoginMetrics metrics,
        ILogger<CredentialVerifier> logger,
        TimeProvider time)
    {
        _users = users;
        _signIn = signIn;
        _decoy = decoy;
        _timing = timing.Value;
        _security = security;
        _metrics = metrics;
        _logger = logger;
        _time = time;
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
        // Prazo em tempo real, como o piso: cada fase tem seu teto (fila 30 s, lock 5 s), e sem um prazo do pedido
        // inteiro as fases somavam mais de um minuto. Ele vale só para as esperas (filas, lock e piso). Gravar o
        // resultado e confirmar a transação usam o token do pedido: cancelá-los depois do hash desfaria a falha, e a
        // tentativa sairia de graça do bloqueio (TM-01). O hash é síncrono e não cancela: o pior caso passa do prazo
        // por um hash mais as gravações.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timing.LoginTimeout);
        try
        {
            return await VerifyWithinDeadlineAsync(email, password, secondFactor, deadline.Token, cancellationToken);
        }
        catch (OperationCanceledException expired)
            when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new AccountBusyException(_timing.LoginTimeout, expired);
        }
    }

    // waiting: esperas (filas, lock, piso), com o prazo do login; writing: gravações depois do hash, só o do pedido.
    private async Task<LoginOutcome> VerifyWithinDeadlineAsync(
        EmailAddress email,
        string password,
        SecondFactorProof? secondFactor,
        CancellationToken waiting,
        CancellationToken writing)
    {
        await using var turn = await _security.EnterLoginAsync(email, waiting);
        // Piso por vez, não por chegada: a espera anterior não pode consumir a proteção do próximo login.
        // CheckAsync encerra a transação antes deste piso, portanto o atraso não prende conexão.
        var startedAt = Stopwatch.GetTimestamp();
        var outcome = await CheckAsync(email, password, secondFactor, waiting, writing);
        if (outcome is LoginOutcome.InvalidCredentials)
        {
            await WaitForFailureFloorAsync(startedAt, waiting);
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
        if (user is null)
        {
            return new ReauthenticationOutcome.Invalid();
        }

        await using var check = await _security.LockAccountAsync(user, cancellationToken);
        var outcome = await CheckReauthenticationAsync(user, password, authenticatorCode, cancellationToken);
        await check.CommitAsync(cancellationToken);
        return outcome;
    }

    private async Task<ReauthenticationOutcome> CheckReauthenticationAsync(
        DuoraUser user,
        string password,
        string? authenticatorCode,
        CancellationToken cancellationToken)
    {
        var wasLockedOut = await _users.IsLockedOutAsync(user);
        if (!(await _signIn.CheckPasswordSignInAsync(user, password, lockoutOnFailure: true)).Succeeded)
        {
            await _security.RecordPasswordFailureAsync(user, PasswordCheck.Reauthentication, wasLockedOut, cancellationToken);
            return new ReauthenticationOutcome.Invalid();
        }

        if (!user.TwoFactorEnabled)
        {
            return new ReauthenticationOutcome.Verified(WithAuthenticator: false);
        }

        if (authenticatorCode is null)
        {
            await _security.RecordSecondFactorRequiredAsync(user, PasswordCheck.Reauthentication, cancellationToken);
            return new ReauthenticationOutcome.Invalid();
        }

        var proved = await _security.VerifySecondFactorAsync(
            user,
            new SecondFactorProof.AuthenticatorCode(authenticatorCode),
            cancellationToken);
        return proved == SecondFactorKind.Authenticator
            ? new ReauthenticationOutcome.Verified(WithAuthenticator: true)
            : new ReauthenticationOutcome.Invalid();
    }

    private async Task<LoginOutcome> CheckAsync(
        EmailAddress email,
        string password,
        SecondFactorProof? secondFactor,
        CancellationToken waiting,
        CancellationToken writing)
    {
        var user = await _users.FindByEmailAsync(email.Value);
        if (user is null)
        {
            _decoy.Verify(_users.PasswordHasher, password);
            RecordLoginWithoutConfirmedAccount();
            return new LoginOutcome.InvalidCredentials();
        }

        // Conta travada durante toda a verificação: o bloqueio (ler, somar, gravar) e o passo TOTP não perdem
        // atualização sob logins paralelos (B1 e B2 da revisão do #38). A falha também é confirmada, para contar.
        await using var check = await _security.LockAccountAsync(user, waiting);
        var outcome = await CheckLockedAsync(user, password, secondFactor, writing);
        await check.CommitAsync(writing);
        return outcome;
    }

    private async Task<LoginOutcome> CheckLockedAsync(
        DuoraUser user,
        string password,
        SecondFactorProof? secondFactor,
        CancellationToken cancellationToken)
    {
        // Lido da entidade já relida sob o lock, sem ida ao banco: o caminho da falha continua com o mesmo trabalho.
        var wasLockedOut = await _users.IsLockedOutAsync(user);
        var result = await _signIn.CheckPasswordSignInAsync(user, user.PasswordToVerify(password), lockoutOnFailure: true);
        if (result.IsNotAllowed)
        {
            // Cadastro pendente: pode ser de qualquer pessoa, então conta como o e-mail sem conta.
            _decoy.Verify(_users.PasswordHasher, password);
            RecordLoginWithoutConfirmedAccount();
            return new LoginOutcome.InvalidCredentials();
        }

        if (!result.Succeeded)
        {
            if (result.IsLockedOut)
            {
                _decoy.Verify(_users.PasswordHasher, password);
            }

            await _security.RecordPasswordFailureAsync(user, PasswordCheck.Login, wasLockedOut, cancellationToken);
            return new LoginOutcome.InvalidCredentials();
        }

        if (user.HasExpiredTemporaryPassword(_time.GetUtcNow()))
        {
            // Senha temporária do bootstrap vencida (D4): responde e audita como senha errada; o operador reemite.
            await _security.RecordPasswordFailureAsync(user, PasswordCheck.Login, wasLockedOut: false, cancellationToken);
            return new LoginOutcome.InvalidCredentials();
        }

        var proved = SecondFactorKind.None;
        if (user.TwoFactorEnabled)
        {
            if (secondFactor is null)
            {
                await _security.RecordSecondFactorRequiredAsync(user, PasswordCheck.Login, cancellationToken);
                return new LoginOutcome.MfaRequired();
            }

            proved = await _security.VerifySecondFactorAsync(user, secondFactor, cancellationToken);
            if (proved == SecondFactorKind.None)
            {
                return new LoginOutcome.InvalidCredentials();
            }
        }

        var policy = AccountRoles.SessionPolicyFor(await _users.GetRolesAsync(user));
        await _security.RecordLoginSucceededAsync(user, cancellationToken);
        return new LoginOutcome.Verified(new VerifiedAccount(
            user.Id,
            await _users.GetSecurityStampAsync(user),
            policy,
            proved));
    }

    /// <summary>
    /// E-mail sem conta ou cadastro pendente: métrica e log, nunca linha na auditoria (não há ator legítimo, e a tabela
    /// append-only cresceria sem limite). Os dois casos fazem o mesmo, sem e-mail nem IP; o trace id do log liga ao
    /// pedido.
    /// </summary>
    private void RecordLoginWithoutConfirmedAccount()
    {
        _metrics.RecordFailedUnknownAccount();
        LogLoginWithoutConfirmedAccount(_logger);
    }

    // Debug: a contagem fica na métrica; o log só serve para investigar um pedido pelo trace id.
    [LoggerMessage(Level = LogLevel.Debug, Message = "Login sem conta confirmada")]
    private static partial void LogLoginWithoutConfirmedAccount(ILogger logger);

    /// <summary>
    /// Espera em tempo real, não no <see cref="TimeProvider"/> da aplicação: o piso protege contra quem mede a
    /// resposta de fora, e um relógio falso em teste não pode congelá-lo.
    /// </summary>
    private Task WaitForFailureFloorAsync(long startedAt, CancellationToken cancellationToken)
    {
        var floor = _timing.FailedLoginMinimumDuration + (_timing.FailedLoginJitter * Random.Shared.NextDouble());
        return WaitForFailureFloorAsync(startedAt, floor, TimeProvider.System, cancellationToken);
    }

    internal static async Task WaitForFailureFloorAsync(
        long startedAt, TimeSpan floor, TimeProvider time, CancellationToken cancellationToken)
    {
        // Task.Delay trunca frações de milissegundo. Arredonda para cima e confere o relógio
        // monotônico depois de cada despertar: um timer concluído não prova que o piso passou.
        while (floor - time.GetElapsedTime(startedAt) is var remaining && remaining > TimeSpan.Zero)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Ceiling(remaining.TotalMilliseconds)), time, cancellationToken);
        }
    }
}
