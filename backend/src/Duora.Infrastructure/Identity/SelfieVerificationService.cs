using Duora.Core.Identity;
using Duora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Duora.Infrastructure.Identity;

public sealed record SelfieStatus(Guid? Id, string State, string NextStep, string? FeedbackCode, DateTimeOffset? ExpiresAt);

/// <summary>Medidas do DetectFaces (Attributes DEFAULT) no frame de referência: brilho e nitidez 0–100, pose em graus.</summary>
public readonly record struct SelfieFaceQuality(float Brightness, float Sharpness, float Pitch, float Yaw, float Roll);

/// <summary>
/// Configuração da selfie. Os padrões são a política aprovada pelo dono em 28/10/2026 (issue #93, ADR-0029), versionada
/// em <see cref="PolicyVersion"/>: mudar um limiar exige nova versão. Fontes oficiais da AWS:
/// liveness 80 = faixa "alta (e.g., 80 or 90)" do AWS AI Service Card do Face Liveness, indicada contra injeção digital
/// (deepfake, vídeo gravado); pose = "Recommendations for facial comparison input images" (pitch abaixo de 30° para
/// baixo, yaw abaixo de 45°), aplicando o limite mais estreito de pitch nos dois sentidos. A AWS não publica número
/// para brilho, nitidez nem roll ("no restriction on the roll"): esses pisos são do projeto, baixos de propósito para
/// só recusar o frame claramente escuro, borrado ou deitado, já que o próprio Face Liveness escolhe o melhor frame.
/// </summary>
public sealed class SelfieProviderOptions
{
    private const double MaximumProviderPoseDegrees = 180;

    public bool Enabled { get; set; }
    public string Region { get; set; } = "us-east-1";
    public string PolicyVersion { get; set; } = "aws-docs-2026-10-28";

    /// <summary>Confiança mínima do Face Liveness, inclusiva (0–100).</summary>
    public double MinimumLiveness { get; set; } = 80;

    /// <summary>Brilho mínimo do rosto, inclusivo (0–100).</summary>
    public double MinimumBrightness { get; set; } = 30;

    /// <summary>Nitidez mínima do rosto, inclusiva (0–100).</summary>
    public double MinimumSharpness { get; set; } = 20;

    /// <summary>|yaw| precisa ficar estritamente abaixo deste valor, em graus.</summary>
    public double MaximumYawDegrees { get; set; } = 45;

    /// <summary>|pitch| precisa ficar estritamente abaixo deste valor, em graus (o limite mais estreito da AWS, para baixo).</summary>
    public double MaximumPitchDegrees { get; set; } = 30;

    /// <summary>|roll| precisa ficar estritamente abaixo deste valor, em graus. A AWS não restringe roll: limite do projeto.</summary>
    public double MaximumRollDegrees { get; set; } = 45;

    public string CaptureRoleArn { get; set; } = "";
    public bool PrivacyApproved { get; set; }

    public bool IsValid() => !Enabled || (PrivacyApproved && !string.IsNullOrWhiteSpace(PolicyVersion)
        && PolicyVersion.Length <= 64 && Region == "us-east-1"
        && MinimumLiveness is > 0 and <= 100 && MinimumBrightness is > 0 and <= 100
        && MinimumSharpness is > 0 and <= 100 && IsPoseLimit(MaximumYawDegrees)
        && IsPoseLimit(MaximumPitchDegrees) && IsPoseLimit(MaximumRollDegrees)
        && CaptureRoleArn.StartsWith("arn:aws:iam::", StringComparison.Ordinal));

    public bool AcceptsLiveness(float confidence) => float.IsFinite(confidence) && confidence >= MinimumLiveness;

    public bool AcceptsFace(SelfieFaceQuality face) => float.IsFinite(face.Brightness) && float.IsFinite(face.Sharpness)
        && face.Brightness >= MinimumBrightness && face.Sharpness >= MinimumSharpness
        && IsWithin(face.Yaw, MaximumYawDegrees) && IsWithin(face.Pitch, MaximumPitchDegrees)
        && IsWithin(face.Roll, MaximumRollDegrees);

    private static bool IsPoseLimit(double limit) => limit is > 0 and <= MaximumProviderPoseDegrees;

    private static bool IsWithin(float angle, double limit) => float.IsFinite(angle) && Math.Abs(angle) < limit;
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
