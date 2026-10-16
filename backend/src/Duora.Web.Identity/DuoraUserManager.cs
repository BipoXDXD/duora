using System.Security.Cryptography;
using Duora.Infrastructure.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Duora.Web.Identity;

/// <summary>
/// UserManager do processo web, que protege a chave do autenticador TOTP com Data Protection antes de gravá-la
/// (decisão D2 do auth.md). O Identity 10 a guarda em claro em <c>identity.user_tokens</c>: um dump do banco daria
/// o segundo fator. Com a proteção, é preciso o dump e o key ring juntos.
/// </summary>
/// <remarks>
/// Custo aceito: perder o key ring (que já está no inventário de backup, spec 18.3) invalida o TOTP de todas as
/// contas; a chave ilegível é tratada como ausente, e a conta privilegiada passa pelo procedimento operacional de
/// recuperação de MFA (TM-22). Só o web registra este manager; o worker nunca lê a chave.
/// </remarks>
internal sealed partial class DuoraUserManager : UserManager<DuoraUser>
{
    private readonly IDataProtector _protector;
    private readonly ILogger<DuoraUserManager> _logger;

    public DuoraUserManager(
        IUserStore<DuoraUser> store,
        IOptions<IdentityOptions> optionsAccessor,
        IPasswordHasher<DuoraUser> passwordHasher,
        IEnumerable<IUserValidator<DuoraUser>> userValidators,
        IEnumerable<IPasswordValidator<DuoraUser>> passwordValidators,
        ILookupNormalizer keyNormalizer,
        IdentityErrorDescriber errors,
        IServiceProvider services,
        ILogger<DuoraUserManager> logger,
        IDataProtectionProvider dataProtection)
        : base(store, optionsAccessor, passwordHasher, userValidators, passwordValidators, keyNormalizer, errors, services, logger)
    {
        _protector = dataProtection.CreateProtector("Duora.Identity.AuthenticatorKey.v1");
        _logger = logger;
    }

    /// <summary>Mesmo efeito do Identity (chave nova e security stamp novo), mas grava a chave protegida.</summary>
    public override async Task<IdentityResult> ResetAuthenticatorKeyAsync(DuoraUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        ThrowIfDisposed();
        var store = Store as IUserAuthenticatorKeyStore<DuoraUser>
            ?? throw new NotSupportedException("O store não guarda chave de autenticador.");
        await store.SetAuthenticatorKeyAsync(user, _protector.Protect(GenerateNewAuthenticatorKey()), CancellationToken);
        return await UpdateSecurityStampAsync(user);
    }

    public override async Task<string?> GetAuthenticatorKeyAsync(DuoraUser user)
    {
        if (await base.GetAuthenticatorKeyAsync(user) is not { } protectedKey)
        {
            return null;
        }

        try
        {
            return _protector.Unprotect(protectedKey);
        }
        catch (CryptographicException exception)
        {
            // Key ring perdido ou trocado, ou valor gravado sem proteção: o TOTP falha fechado.
            LogUnreadableAuthenticatorKey(_logger, exception);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Chave do autenticador ilegível com o key ring atual; TOTP tratado como ausente")]
    private static partial void LogUnreadableAuthenticatorKey(ILogger logger, Exception exception);
}
