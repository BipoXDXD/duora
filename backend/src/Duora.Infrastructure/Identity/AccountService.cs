using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Duora.Core.Identity;
using Duora.Infrastructure.Email;
using Duora.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

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

/// <summary>
/// Casos de uso da conta sobre o ASP.NET Core Identity (spec 8.2). Toda resposta para quem não é o titular é
/// neutra, e os e-mails saem pela outbox na mesma transação que os justifica, nunca por chamada direta.
/// </summary>
public sealed class AccountService
{
    private const string PostgresUniqueViolation = "23505";

    private readonly DuoraDbContext _db;
    private readonly UserManager<DuoraUser> _users;
    private readonly SignInManager<DuoraUser> _signIn;
    private readonly LoginSessions _sessions;
    private readonly CredentialTiming _timing;
    private readonly TimeProvider _time;

    internal AccountService(
        DuoraDbContext db,
        UserManager<DuoraUser> users,
        SignInManager<DuoraUser> signIn,
        LoginSessions sessions,
        CredentialTiming timing,
        TimeProvider time)
    {
        _db = db;
        _users = users;
        _signIn = signIn;
        _sessions = sessions;
        _timing = timing;
        _time = time;
    }

    /// <summary>
    /// Cria a conta e pede o e-mail de confirmação; se o e-mail já tem conta, avisa o titular por e-mail. Quem
    /// chamou não fica sabendo qual dos dois aconteceu.
    /// </summary>
    public async Task RegisterAsync(
        EmailAddress email,
        string password,
        string termsVersion,
        string privacyVersion,
        CancellationToken cancellationToken)
    {
        var existing = await _users.FindByEmailAsync(email.Value);
        if (existing is not null)
        {
            // O caminho novo calcula um hash; este também, para o tempo de resposta não denunciar a conta.
            _timing.SpendHashingTime(password);
            await NotifyExistingAccountAsync(existing, email, cancellationToken);
            return;
        }

        if (!await TryCreateAccountAsync(email, password, termsVersion, privacyVersion, cancellationToken))
        {
            // Outro cadastro com o mesmo e-mail ganhou a corrida (índice único); segue como conta existente.
            _db.ChangeTracker.Clear();
            var winner = await _users.FindByEmailAsync(email.Value)
                ?? throw new InvalidOperationException("Conta duplicada sumiu entre a violação de unicidade e a releitura.");
            await NotifyExistingAccountAsync(winner, email, cancellationToken);
        }
    }

    public async Task<bool> ConfirmEmailAsync(Guid userId, string linkToken, CancellationToken cancellationToken)
    {
        var user = await _users.FindByIdAsync(userId.ToString("D"));
        if (user is null || !TryDecodeLinkToken(linkToken, out var token))
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

    public async Task RequestPasswordResetAsync(EmailAddress email, CancellationToken cancellationToken)
    {
        var user = await _users.FindByEmailAsync(email.Value);
        if (user is null)
        {
            return;
        }

        var token = await _users.GeneratePasswordResetTokenAsync(user);
        Enqueue(AccountEmailKind.PasswordReset, email, user.Id, token);
        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Troca a senha com o token do e-mail. O Identity troca o security stamp, o que já invalida todas as sessões;
    /// a revogação explícita deixa o motivo registrado. O bloqueio por tentativas é zerado, para quem foi
    /// bloqueado por um atacante recuperar o acesso pelo e-mail.
    /// </summary>
    public async Task<bool> ResetPasswordAsync(
        Guid userId,
        string linkToken,
        string newPassword,
        CancellationToken cancellationToken)
    {
        var user = await _users.FindByIdAsync(userId.ToString("D"));
        if (user is null || !TryDecodeLinkToken(linkToken, out var token))
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

    private async Task<bool> TryCreateAccountAsync(
        EmailAddress email,
        string password,
        string termsVersion,
        string privacyVersion,
        CancellationToken cancellationToken)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        var now = _time.GetUtcNow();
        var user = DuoraUser.Register(email, now);
        try
        {
            var created = await _users.CreateAsync(user, password);
            if (!created.Succeeded)
            {
                if (created.Errors.Any(IsDuplicate))
                {
                    return false;
                }

                // A política de senha é validada na fronteira com as mesmas regras; chegar aqui é defeito.
                throw new InvalidOperationException(
                    "Identity recusou o cadastro: " + string.Join(", ", created.Errors.Select(e => e.Code)));
            }

            _db.Add(ConsentRecord.Accept(user.Id, LegalDocument.Terms, termsVersion, now));
            _db.Add(ConsentRecord.Accept(user.Id, LegalDocument.Privacy, privacyVersion, now));
            var token = await _users.GenerateEmailConfirmationTokenAsync(user);
            Enqueue(AccountEmailKind.EmailConfirmation, email, user.Id, token);
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresUniqueViolation })
        {
            return false;
        }
    }

    private async Task NotifyExistingAccountAsync(DuoraUser user, EmailAddress email, CancellationToken cancellationToken)
    {
        // Conta ainda não confirmada: o aviso útil é um novo link de confirmação, não "você já tem conta".
        if (!user.EmailConfirmed)
        {
            var token = await _users.GenerateEmailConfirmationTokenAsync(user);
            Enqueue(AccountEmailKind.EmailConfirmation, email, user.Id, token);
        }
        else
        {
            Enqueue(AccountEmailKind.RegistrationAttemptOnExistingAccount, email, user.Id, token: null);
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

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

    private static bool IsDuplicate(IdentityError error) =>
        error.Code is nameof(IdentityErrorDescriber.DuplicateEmail) or nameof(IdentityErrorDescriber.DuplicateUserName);
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
