using Duora.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;

namespace Duora.Infrastructure.Identity;

/// <summary>
/// Store do Identity sobre o <see cref="DuoraDbContext"/>, com uma diferença: os códigos de recuperação do MFA
/// são gravados só como hash do <see cref="IPasswordHasher{TUser}"/> (PBKDF2), um por código (decisão D3 do
/// auth.md). O store padrão os guarda em claro, e um dump do banco daria o segundo fator. Os códigos têm cerca de
/// 47 bits (10 caracteres de um alfabeto de 26), pouco para um hash rápido resistir offline.
/// </summary>
/// <remarks>
/// Resgatar um código verifica até <see cref="AccountSecurityService.RecoveryCodeCount"/> hashes. É o caminho raro
/// (perda do autenticador), já limitado pelo rate limit do login. Dois resgates simultâneos do mesmo código não
/// passam os dois por duas defesas: o login trava a linha da conta (<see cref="AccountCheck"/>), e, mesmo sem o
/// lock, o <c>UpdateAsync</c> do UserManager confere o concurrency stamp da conta e o perdedor falharia sem gravar
/// (o token e a linha da conta saem no mesmo SaveChanges). Este store depende desse acoplamento: trocar o
/// <c>RedeemCodeAsync</c> por SQL direto exigiria a condição explícita no UPDATE.
/// </remarks>
internal sealed class DuoraUserStore(
    DuoraDbContext context,
    IPasswordHasher<DuoraUser> hasher,
    IdentityErrorDescriber? describer = null)
    : UserStore<DuoraUser, IdentityRole<Guid>, DuoraDbContext, Guid>(context, describer)
{
    /// <summary>Provider e nome próprios: um valor antigo do formato em claro do Identity nunca é lido como hash.</summary>
    internal const string RecoveryCodeProvider = "[DuoraUserStore]";

    internal const string RecoveryCodeName = "RecoveryCodeHashes";

    private const char Separator = ';';

    public override Task ReplaceCodesAsync(
        DuoraUser user,
        IEnumerable<string> recoveryCodes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(recoveryCodes);
        var hashes = recoveryCodes.Select(code => hasher.HashPassword(user, Normalize(code)));
        return SetTokenAsync(user, RecoveryCodeProvider, RecoveryCodeName, string.Join(Separator, hashes), cancellationToken);
    }

    public override async Task<bool> RedeemCodeAsync(DuoraUser user, string code, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(code);
        var hashes = await ReadHashesAsync(user, cancellationToken);
        var normalized = Normalize(code);
        var match = hashes.FindIndex(hash =>
            hasher.VerifyHashedPassword(user, hash, normalized) != PasswordVerificationResult.Failed);
        if (match < 0)
        {
            return false;
        }

        hashes.RemoveAt(match);
        await SetTokenAsync(user, RecoveryCodeProvider, RecoveryCodeName, string.Join(Separator, hashes), cancellationToken);
        return true;
    }

    public override async Task<int> CountCodesAsync(DuoraUser user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        return (await ReadHashesAsync(user, cancellationToken)).Count;
    }

    /// <summary>
    /// Os códigos saem em maiúsculas com um hífen no meio; quem digita em minúsculas, sem o hífen ou com espaços não
    /// erra por isso.
    /// </summary>
    private static string Normalize(string code) =>
        string.Concat(code.Where(c => c != '-' && !char.IsWhiteSpace(c))).ToUpperInvariant();

    private async Task<List<string>> ReadHashesAsync(DuoraUser user, CancellationToken cancellationToken)
    {
        var stored = await GetTokenAsync(user, RecoveryCodeProvider, RecoveryCodeName, cancellationToken) ?? "";
        return [.. stored.Split(Separator, StringSplitOptions.RemoveEmptyEntries)];
    }
}
