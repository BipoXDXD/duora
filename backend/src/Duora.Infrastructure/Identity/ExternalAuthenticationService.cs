using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Duora.Core.Identity;
using Duora.Core.Notifications;
using Duora.Infrastructure.Audit;
using Duora.Infrastructure.Email;
using Duora.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Duora.Infrastructure.Identity;

public sealed record ExternalFlowView(string NextStep, DateTimeOffset ExpiresAt, string? Email = null);
public sealed record ExternalAccount(Guid UserId, string SecurityStamp, SecondFactorKind SecondFactor);
public sealed record ExternalCompletion(string Outcome, ExternalAccount? Account = null);
public sealed record ExternalCredentialChange(string Outcome, string? SecurityStamp = null);
public sealed record ExternalLoginInfo(string Provider, DateTimeOffset LinkedAt);
public sealed class ExternalFlowInvalidException : Exception { }

/// <summary>
/// Application authorization happens here, after the official handler validated the provider. Account locks
/// precede flow locks everywhere; callbacks only lock their flow and never wait for a business account.
/// </summary>
public sealed class ExternalAuthenticationService
{
    public const string Provider = "google-oidc";
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private readonly DuoraDbContext _db;
    private readonly UserManager<DuoraUser> _users;
    private readonly AccountSecurityService _security;
    private readonly IExternalIdentityProtection _protection;
    private readonly TimeProvider _time;
    private readonly AuditTrail _audit;
    private readonly ExternalAuthenticationMetrics _metrics;

    internal ExternalAuthenticationService(DuoraDbContext db, UserManager<DuoraUser> users,
        AccountSecurityService security, IExternalIdentityProtection protection, TimeProvider time, ExternalAuthenticationMetrics metrics)
    {
        _db = db;
        _users = users;
        _security = security;
        _protection = protection;
        _time = time;
        _audit = new AuditTrail(db, time);
        _metrics = metrics;
    }

    public async Task<string?> CreateAsync(string purpose, Guid? userId, Guid? sessionId, CancellationToken ct)
    {
        if (purpose is not ("Login" or "Register" or "Link"))
        {
            return null;
        }

        if (purpose == "Link" && (userId is null || sessionId is null))
        {
            return null;
        }

        string? stamp = null;
        if (purpose == "Link")
        {
            var user = await _users.FindByIdAsync(userId!.Value.ToString("D"));
            if (user is null || !await CanLinkAsync(user, sessionId!.Value, ct))
            {
                return null;
            }

            stamp = user.SecurityStamp;
        }
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        // A bounded shared admission lock avoids an unbounded number of durable anonymous flows under races.
        var admitted = await _db.Database.SqlQuery<bool>($"select pg_try_advisory_xact_lock(18460276) as \"Value\"").SingleAsync(ct);
        if (!admitted)
        {
            return null;
        }

        var now = _time.GetUtcNow();
        await _db.Set<ExternalAuthFlow>().Where(f => f.ExpiresAt <= now).ExecuteDeleteAsync(ct);
        if (await _db.Set<ExternalAuthFlow>().CountAsync(ct) >= 10000)
        {
            return null;
        }

        var secret = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var flow = new ExternalAuthFlow
        {
            Id = Guid.CreateVersion7(now),
            BrowserHash = Hash(secret),
            Purpose = purpose,
            UserId = userId,
            SessionId = sessionId,
            ExpectedStamp = stamp,
            CreatedAt = now,
            ExpiresAt = now + Lifetime,
        };
        _db.Add(flow);
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return $"{flow.Id:D}.{secret}";
    }

    public async Task<Guid?> ChallengeAsync(string? cookie, CancellationToken ct)
    {
        var flow = await FindAsync(cookie, ct);
        if (flow is null)
        {
            return null;
        }

        var changed = await _db.Set<ExternalAuthFlow>().Where(f => f.Id == flow.Id && f.Stage == "Created" && f.ExpiresAt > _time.GetUtcNow())
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.Stage, "Challenged"), ct);
        return changed == 1 ? flow.Id : null;
    }

    public async Task<bool> StageProofAsync(string? cookie, Guid id, ExternalIdentity identity, Guid? currentSession, CancellationToken ct)
    {
        var flow = await FindAsync(cookie, ct);
        if (flow is null || flow.Id != id || flow.Stage != "Challenged")
        {
            return false;
        }

        if (flow.Purpose == "Link" && flow.SessionId != currentSession)
        {
            return false;
        }

        if (flow.Purpose != "Link" && currentSession is not null)
        {
            return false;
        }

        var candidate = flow.Purpose == "Login" ? await _users.FindByLoginAsync(Provider, identity.Subject) : null;
        var protectedProof = _protection.Protect(identity);
        var changed = await _db.Set<ExternalAuthFlow>().Where(f => f.Id == id && f.Stage == "Challenged" && f.ExpiresAt > _time.GetUtcNow())
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.Stage, "Validated").SetProperty(f => f.ProtectedProof, protectedProof)
                .SetProperty(f => f.UserId, flow.Purpose == "Login" ? candidate == null ? null : candidate.Id : flow.UserId)
                .SetProperty(f => f.ExpectedStamp, flow.Purpose == "Login" ? candidate == null ? null : candidate.SecurityStamp : flow.ExpectedStamp), ct);
        return changed == 1;
    }

    public async Task InvalidateBrowserAsync(string cookie, CancellationToken ct)
    {
        var flow = await FindAsync(cookie, ct);
        if (flow is null)
        {
            return;
        }
        await _db.Set<ExternalAuthFlow>().Where(f => f.Id == flow.Id && f.Stage != "Complete")
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.Stage, "Failed").SetProperty(f => f.ProtectedProof, (string?)null), ct);
    }

    public async Task FailAsync(string? cookie, CancellationToken ct)
    {
        var flow = await FindAsync(cookie, ct);
        if (flow is null)
        {
            return;
        }

        await _db.Set<ExternalAuthFlow>().Where(f => f.Id == flow.Id && (f.Stage == "Created" || f.Stage == "Challenged"))
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.Stage, "Failed").SetProperty(f => f.ProtectedProof, (string?)null), ct);
    }

    public async Task<ExternalFlowView> ViewAsync(string? cookie, Guid? currentSession, CancellationToken ct)
    {
        var f = await FindAsync(cookie, ct);
        if (f is null)
        {
            return new("Failed", _time.GetUtcNow());
        }

        if ((f.Purpose == "Link" && f.SessionId != currentSession) || (f.Purpose != "Link" && currentSession is not null))
        {
            return new("Failed", f.ExpiresAt);
        }
        var proof = Proof(f);
        var step = f.Stage switch
        {
            "Complete" => "Complete",
            "Registration" => "ConfirmEmail",
            "Validated" when f.Purpose == "Link" => "Link",
            "Validated" when f.Purpose == "Register" => "Register",
            "Validated" when f.UserId is not null => "Login",
            "Validated" => "Unknown",
            _ => "Failed",
        };
        if (step is not ("Failed" or "Complete") && proof is null)
        {
            step = "Failed";
        }

        return new(step, f.ExpiresAt, step == "Register" ? proof?.Email : null);
    }

    public async Task<ExternalCompletion> CompleteLoginAsync(string? cookie, SecondFactorProof? secondFactor, CancellationToken ct)
    {
        var f = await FindAsync(cookie, ct);
        if (f is not { Purpose: "Login", Stage: "Validated", UserId: { } id })
        {
            return new("Invalid");
        }

        var user = await _users.FindByIdAsync(id.ToString("D"));
        if (user is null)
        {
            return new("Invalid");
        }

        await using var check = await _security.LockAccountAsync(user, ct);
        f = await LockFlowAsync(cookie, ct);
        var proof = f is null ? null : Proof(f);
        if (f is not { Purpose: "Login", Stage: "Validated" } || proof is null || f.ExpectedStamp != user.SecurityStamp
            || !await CanEnterAsync(user) || (await _users.FindByLoginAsync(Provider, proof.Subject))?.Id != id)
        {
            return new("Invalid");
        }

        var factor = SecondFactorKind.None;
        if (user.TwoFactorEnabled)
        {
            if (secondFactor is null)
            {
                await AuditAsync(user.Id, AuditAction.ExternalSecondFactorRequired, ct);
                await check.CommitAsync(ct);
                _metrics.Record("login", "mfa_required");
                return new("MfaRequired");
            }

            factor = await _security.VerifySecondFactorAsync(user, secondFactor, ct);
            if (factor == SecondFactorKind.None)
            {
                f.Attempts++;
                if (f.Attempts >= 5)
                { f.Stage = "Failed"; f.ProtectedProof = null; }
                await _db.SaveChangesAsync(ct);
                await AuditAsync(user.Id, AuditAction.ExternalLoginFailed, ct);
                await check.CommitAsync(ct);
                _metrics.Record("login", "rejected");
                return new("Invalid");
            }
        }
        await _users.ResetAccessFailedCountAsync(user);
        f.Stage = "Complete";
        f.ProtectedProof = null;
        await _db.SaveChangesAsync(ct);
        await AuditAsync(user.Id, AuditAction.ExternalLoginSucceeded, ct);
        await check.CommitAsync(ct);
        _metrics.Record("login", "completed");
        return new("Completed", new(user.Id, user.SecurityStamp!, factor));
    }

    public async Task<ExternalCredentialChange> LinkAsync(string? cookie, Guid userId, Guid sessionId, CancellationToken ct)
    {
        var user = await _users.FindByIdAsync(userId.ToString("D"));
        if (user is null)
        {
            return new("Invalid");
        }

        await using var check = await _security.LockAccountAsync(user, ct);
        var f = await LockFlowAsync(cookie, ct);
        var proof = f is null ? null : Proof(f);
        if (f is not { Purpose: "Link", Stage: "Validated" } || f.UserId != userId || f.SessionId != sessionId
            || f.ExpectedStamp != user.SecurityStamp || proof is null || !await CanLinkAsync(user, sessionId, ct))
        {
            return new("Invalid");
        }

        var already = await _users.FindByLoginAsync(Provider, proof.Subject);
        if (already is not null || await _db.Set<IdentityUserLogin<Guid>>().AnyAsync(l => l.UserId == userId && l.LoginProvider == Provider, ct))
        {
            return new("Conflict");
        }

        _db.Add(new IdentityUserLogin<Guid> { UserId = userId, LoginProvider = Provider, ProviderKey = proof.Subject, ProviderDisplayName = "Google" });
        f.Stage = "Complete";
        f.ProtectedProof = null;
        var stamp = await CredentialChangedAsync(user, AuditAction.ExternalLinked, SecurityChange.GoogleLinked, ct);
        await check.CommitAsync(ct);
        return new("Completed", stamp);
    }

    public async Task<ExternalCredentialChange> UnlinkAsync(Guid userId, Guid sessionId, CancellationToken ct)
    {
        var user = await _users.FindByIdAsync(userId.ToString("D"));
        if (user is null)
        {
            return new("Invalid");
        }

        await using var check = await _security.LockAccountAsync(user, ct);
        if (!await CanLinkAsync(user, sessionId, ct))
        {
            return new("Invalid");
        }

        var count = await _db.Set<IdentityUserLogin<Guid>>().Where(l => l.UserId == userId && l.LoginProvider == Provider).ExecuteDeleteAsync(ct);
        if (count == 0)
        {
            return new("Absent");
        }

        var stamp = await CredentialChangedAsync(user, AuditAction.ExternalUnlinked, SecurityChange.GoogleUnlinked, ct);
        await check.CommitAsync(ct);
        return new("Completed", stamp);
    }

    public async Task<IReadOnlyList<ExternalLoginInfo>> ListAsync(Guid userId, CancellationToken ct)
    {
        if (!await _db.Set<IdentityUserLogin<Guid>>().AnyAsync(l => l.UserId == userId && l.LoginProvider == Provider, ct))
        {
            return [];
        }

        var at = await _db.Database.SqlQuery<DateTimeOffset>($"select occurred_at as \"Value\" from ops.audit_events where actor_id={userId} and action='identity.external_linked' order by occurred_at desc limit 1").FirstOrDefaultAsync(ct);
        return [new("Google", at)];
    }

    public async Task RegisterAsync(string? cookie, AccountService accounts, string password, string termsVersion, string privacyVersion,
        EmailLanguage language, string? termsLocale, string? privacyLocale, string? termsHash, string? privacyHash, CancellationToken ct)
    {
        var f = await FindAsync(cookie, ct);
        var identity = f is null ? null : Proof(f);
        if (f is not { Purpose: "Register", Stage: "Validated" } || identity is null || !EmailAddress.TryParse(identity.Email, out var email))
        {
            throw new ExternalFlowInvalidException();
        }

        if (await _users.FindByLoginAsync(Provider, identity.Subject) is not null)
        {
            throw new ExternalFlowInvalidException();
        }

        await accounts.RegisterAsync(email, password, termsVersion, privacyVersion, language, ct, termsLocale, privacyLocale, termsHash, privacyHash,
            async (id, stamp, cancellation) =>
            {
                var locked = await LockFlowAsync(cookie, cancellation);
                if (locked is not { Purpose: "Register", Stage: "Validated" })
                {
                    throw new ExternalFlowInvalidException();
                }

                locked.UserId = stamp is null ? null : id;
                locked.ExpectedStamp = stamp;
                locked.Stage = "Registration";
                await _db.SaveChangesAsync(cancellation);
            });
    }

    public async Task<bool> ConfirmRegistrationAsync(string? cookie, AccountService accounts, string code, string password, CancellationToken ct)
    {
        var f = await FindAsync(cookie, ct);
        if (f is not { Purpose: "Register", Stage: "Registration", UserId: { } id })
        {
            return false;
        }

        return await accounts.ConfirmEmailAsync(id, code, password, ct, async (userId, stamp, cancellation) =>
        {
            var locked = await LockFlowAsync(cookie, cancellation);
            var identity = locked is null ? null : Proof(locked);
            if (locked is not { Stage: "Registration", Purpose: "Register" } || locked.UserId != userId || locked.ExpectedStamp != stamp || identity is null)
            {
                return false;
            }

            _db.Add(new IdentityUserLogin<Guid> { UserId = userId, LoginProvider = Provider, ProviderKey = identity.Subject, ProviderDisplayName = "Google" });
            locked.Stage = "Complete";
            locked.ProtectedProof = null;
            await _db.SaveChangesAsync(cancellation);
            await AuditAsync(userId, AuditAction.ExternalLinked, cancellation);
            var user = await _users.FindByIdAsync(userId.ToString("D")) ?? throw new ExternalFlowInvalidException();
            Notice(user, SecurityChange.GoogleLinked);
            await _db.SaveChangesAsync(cancellation);
            return true;
        });
    }

    private async Task<string> CredentialChangedAsync(DuoraUser user, AuditAction action, SecurityChange notice, CancellationToken ct)
    {
        var result = await _users.UpdateSecurityStampAsync(user);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException("External credential stamp update failed.");
        }

        var now = _time.GetUtcNow();
        await _db.Database.ExecuteSqlAsync($"update identity.login_sessions set revoked_at={now}, revocation_reason='external_credential_change' where user_id={user.Id} and revoked_at is null", ct);
        await _db.Set<ExternalAuthFlow>().Where(f => f.UserId == user.Id && f.Stage != "Complete")
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.Stage, "Failed").SetProperty(f => f.ProtectedProof, (string?)null), ct);
        Notice(user, notice);
        await _db.SaveChangesAsync(ct);
        await AuditAsync(user.Id, action, ct);
        return user.SecurityStamp!;
    }

    private void Notice(DuoraUser user, SecurityChange change) => _db.Add(AccountEmailOutbox.CreateSecurityNotice(change,
        user.Email!, user.Id, EmailLanguageTag.Match(user.PreferredLocale) ?? EmailLanguageTag.Default, _time.GetUtcNow(), user.PreferredTimeZone));

    private Task AuditAsync(Guid id, AuditAction action, CancellationToken ct) =>
        _audit.AppendAsync(new AuditRecord(AuditActorKind.User, id, action, "user", id, 46), ct);

    private async Task<bool> CanEnterAsync(DuoraUser user) => user.EmailConfirmed && user.PasswordHash is not null
        && user.TemporaryPasswordExpiresAt is null && user.Status != AccountStatus.DeletionPending
        && !await _users.IsLockedOutAsync(user) && !(await _users.GetRolesAsync(user)).Any(AccountRoles.IsPrivileged);

    private async Task<bool> CanLinkAsync(DuoraUser user, Guid sid, CancellationToken ct)
    {
        if (!await CanEnterAsync(user))
        {
            return false;
        }

        var now = _time.GetUtcNow();
        var session = await _db.Set<LoginSession>().AsNoTracking().SingleOrDefaultAsync(s => s.Id == sid && s.UserId == user.Id, ct);
        return session is not null && session.RevokedAt is null && session.SecurityStamp == user.SecurityStamp
            && session.IdleExpiresAt > now && session.AbsoluteExpiresAt > now
            && RecentAuthentication.IsRecent(session.ReauthenticatedAt, now)
            && (!user.TwoFactorEnabled || RecentAuthentication.IsRecent(session.TotpVerifiedAt, now));
    }

    private ExternalIdentity? Proof(ExternalAuthFlow f) => f.ProtectedProof is { } proof ? _protection.Unprotect(proof) : null;

    private async Task<ExternalAuthFlow?> FindAsync(string? cookie, CancellationToken ct)
    {
        if (!TryCookie(cookie, out var id, out var hash))
        {
            return null;
        }

        var flow = await _db.Set<ExternalAuthFlow>().AsNoTracking().SingleOrDefaultAsync(f => f.Id == id, ct);
        return flow is not null && flow.ExpiresAt > _time.GetUtcNow() && CryptographicOperations.FixedTimeEquals(flow.BrowserHash, hash) ? flow : null;
    }

    private async Task<ExternalAuthFlow?> LockFlowAsync(string? cookie, CancellationToken ct)
    {
        if (_db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Flow lock needs the account transaction.");
        }

        if (!TryCookie(cookie, out var id, out var hash))
        {
            return null;
        }
        // Discard stale tracked read state before taking the authoritative row lock.
        var tracked = _db.ChangeTracker.Entries<ExternalAuthFlow>().FirstOrDefault(e => e.Entity.Id == id);
        if (tracked is not null)
        {
            tracked.State = EntityState.Detached;
        }

        var flow = await _db.Set<ExternalAuthFlow>().FromSql($"select * from identity.external_auth_flows where id={id} for update").SingleOrDefaultAsync(ct);
        return flow is not null && flow.ExpiresAt > _time.GetUtcNow() && CryptographicOperations.FixedTimeEquals(flow.BrowserHash, hash) ? flow : null;
    }

    private static byte[] Hash(string secret) => SHA256.HashData(Encoding.UTF8.GetBytes(secret));
    private static bool TryCookie(string? cookie, out Guid id, out byte[] hash)
    {
        id = default;
        hash = [];
        if (cookie is not { Length: 80 } || cookie[36] != '.' || !Guid.TryParseExact(cookie[..36], "D", out id))
        {
            return false;
        }

        hash = Hash(cookie[37..]);
        return true;
    }
}
