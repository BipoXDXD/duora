using Duora.Core.Identity;
using Duora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Duora.Infrastructure.Identity;

public sealed record SelfieStatus(Guid? Id, string State, string NextStep, string? FeedbackCode, DateTimeOffset? ExpiresAt);

public sealed class SelfieProviderOptions
{
    public bool Enabled { get; set; }
    public string Region { get; set; } = "us-east-1";
    public string PolicyVersion { get; set; } = "";
    public double MinimumLiveness { get; set; }
    public double MinimumBrightness { get; set; }
    public double MinimumSharpness { get; set; }
    public double MaximumPoseDegrees { get; set; }
    public string CaptureRoleArn { get; set; } = "";
    public bool PrivacyApproved { get; set; }

    public bool IsValid() => !Enabled || (PrivacyApproved && !string.IsNullOrWhiteSpace(PolicyVersion)
        && PolicyVersion.Length <= 64 && Region == "us-east-1"
        && MinimumLiveness is > 0 and <= 100 && MinimumBrightness is > 0 and <= 100
        && MinimumSharpness is > 0 and <= 100 && MaximumPoseDegrees is > 0 and <= 90
        && CaptureRoleArn.StartsWith("arn:aws:iam::", StringComparison.Ordinal));
}

public sealed class SelfieVerificationService
{
    private readonly DuoraDbContext _db;
    private readonly SelfieProviderOptions _options;
    private readonly TimeProvider _time;

    internal SelfieVerificationService(DuoraDbContext db, IOptions<SelfieProviderOptions> options, TimeProvider time)
    {
        _db = db;
        _options = options.Value;
        _time = time;
    }

    public async Task<bool> GetEligibilityAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await _db.Users.AsNoTracking().Where(u => u.Id == userId)
            .Select(u => new { u.Status, u.EmailConfirmed }).SingleOrDefaultAsync(cancellationToken);
        if (user is null)
        {
            return false;
        }

        var passed = await _db.Set<SelfieVerificationRecord>().AsNoTracking()
            .AnyAsync(v => v.UserId == userId && v.State == SelfieState.Passed, cancellationToken);
        return ParticipationEligibility.CanParticipate(user.Status, user.EmailConfirmed, passed ? SelfieState.Passed : null);
    }

    public async Task<SelfieStatus> GetCurrentAsync(Guid userId, CancellationToken cancellationToken)
    {
        var row = await _db.Set<SelfieVerificationRecord>().AsNoTracking()
            .Where(v => v.UserId == userId).OrderByDescending(v => v.RequestedAt).ThenByDescending(v => v.Id)
            .FirstOrDefaultAsync(cancellationToken);
        return ToStatus(row);
    }

    public async Task<SelfieStatus?> RequestAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return null;
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        var states = await _db.Database.SqlQuery<string>($"select status as \"Value\" from identity.users where id = {userId} and email_confirmed for update")
            .ToListAsync(cancellationToken);
        if (states.Count != 1 || states[0] is not ("PendingEligibility" or "Active"))
        {
            return null;
        }

        var now = _time.GetUtcNow();
        await _db.Set<SelfieVerificationRecord>().Where(v => v.UserId == userId && v.ExpiresAt <= now
            && (v.State == SelfieState.Pending || v.State == SelfieState.Capturing || v.State == SelfieState.Processing))
            .ExecuteUpdateAsync(set => set.SetProperty(v => v.State, SelfieState.Expired)
                .SetProperty(v => v.Version, v => v.Version + 1), cancellationToken);
        var active = await _db.Set<SelfieVerificationRecord>().AsNoTracking().FirstOrDefaultAsync(v => v.UserId == userId
            && (v.State == SelfieState.Passed || v.State == SelfieState.Pending || v.State == SelfieState.Capturing || v.State == SelfieState.Processing), cancellationToken);
        if (active is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return ToStatus(active);
        }

        var recent = _db.Set<SelfieVerificationRecord>().Where(v => v.UserId == userId && v.RequestedAt > now.AddHours(-1));
        if (await recent.CountAsync(cancellationToken) >= 5
            || await recent.AnyAsync(v => v.RequestedAt > now.AddMinutes(-1), cancellationToken))
        {
            return null;
        }

        var record = new SelfieVerificationRecord
        {
            Id = Guid.CreateVersion7(now),
            UserId = userId,
            State = SelfieState.Pending,
            PolicyVersion = _options.PolicyVersion,
            RequestedAt = now,
            ExpiresAt = now.AddMinutes(3),
        };
        _db.Add(record);
        _db.Add(SelfieVerificationOutbox.Create(record.Id, now, record.ExpiresAt));
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ToStatus(record);
    }

    internal async Task<string?> CaptureReferenceAsync(Guid userId, Guid id, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        return await _db.Set<SelfieVerificationRecord>().Where(v => v.Id == id && v.UserId == userId
            && v.State == SelfieState.Capturing && v.ExpiresAt > now
            && _db.Users.Any(u => u.Id == userId && u.EmailConfirmed
                && (u.Status == AccountStatus.PendingEligibility || u.Status == AccountStatus.Active)))
            .Select(v => v.ProviderReference).SingleOrDefaultAsync(cancellationToken);
    }

    private SelfieStatus ToStatus(SelfieVerificationRecord? row)
    {
        if (row is null)
        {
            return new(null, "NotStarted", "Start", null, null);
        }

        var state = row.State;
        if (state is SelfieState.Pending or SelfieState.Capturing or SelfieState.Processing && row.ExpiresAt <= _time.GetUtcNow())
        {
            state = SelfieState.Expired;
        }

        return new(row.Id, state.ToString(), state switch
        {
            SelfieState.Passed => "None",
            SelfieState.Capturing => "Capture",
            SelfieState.Pending or SelfieState.Processing => "Wait",
            _ => "Recapture",
        }, row.FeedbackCode, state == SelfieState.Passed ? null : row.ExpiresAt);
    }
}
