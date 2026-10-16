using System.Buffers.Text;
using System.Security.Cryptography;
using Duora.Core.Identity;
using Duora.Infrastructure.Audit;
using Duora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Duora.Infrastructure.Identity;

/// <summary>Estado da verificação como a pessoa o vê (API-P06): estado mínimo, próximo passo e, se houver, o fluxo.</summary>
public sealed record AgeVerificationStatus(string State, AgeVerificationNextStep NextStep, Uri? ContinueUrl)
{
    /// <summary>Valor de <see cref="State"/> quando a pessoa nunca pediu verificação.</summary>
    public const string NotStarted = "NotStarted";
}

/// <summary>Resultado de API-P05.</summary>
public abstract record AgeVerificationRequestResult
{
    private AgeVerificationRequestResult()
    {
    }

    /// <summary>Solicitação criada agora ou a que já estava aberta (idempotência natural, ADR-0024 decisão 6).</summary>
    public sealed record Accepted(AgeVerificationStatus Status) : AgeVerificationRequestResult;

    /// <summary>A conta está elegível, em revisão, rejeitada ou fora do fluxo (restrita, suspensa, em exclusão).</summary>
    public sealed record NotAllowed : AgeVerificationRequestResult;

    /// <summary>Nenhum fornecedor configurado; a conta continua pendente (TM-40).</summary>
    public sealed record ProviderUnavailable : AgeVerificationRequestResult;
}

/// <summary>
/// Casos de uso da verificação etária no Identity (API-P05, API-P06 e <c>GetEligibility</c>). O fornecedor nunca é
/// chamado aqui: a solicitação e o pedido ao worker comitam juntos (ADR-0024, decisão 1), e o resultado só chega
/// pelo webhook autenticado.
/// </summary>
public sealed class AgeVerificationService
{
    private const int NonceBytes = 32;
    private const string TargetType = "age_verification";

    private readonly DuoraDbContext _db;
    private readonly AgeVerificationOptions _options;
    private readonly AgeVerificationPolicy _policy;
    private readonly string _providerName;
    private readonly TimeProvider _time;

    internal AgeVerificationService(
        DuoraDbContext db,
        AgeVerificationOptions options,
        string providerName,
        TimeProvider time)
    {
        _db = db;
        _options = options;
        _policy = options.ToPolicy();
        _providerName = providerName;
        _time = time;
    }

    /// <summary>
    /// Abre uma solicitação para a pessoa, ou devolve a que está aberta. A linha da conta é travada primeiro, o que
    /// serializa pedidos simultâneos da mesma pessoa e segue a ordem conta → solicitação também usada pelo webhook.
    /// </summary>
    public async Task<AgeVerificationRequestResult> RequestAsync(
        Guid userId,
        Guid loginSessionId,
        CancellationToken cancellationToken)
    {
        if (!_options.IsEnabled)
        {
            return new AgeVerificationRequestResult.ProviderUnavailable();
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        var now = _time.GetUtcNow();
        if (await LockAccountStatusAsync(userId, cancellationToken) is not { } account)
        {
            return new AgeVerificationRequestResult.NotAllowed();
        }

        var latest = await FindLatestAsync(userId, cancellationToken);
        var eligibility = AgeEligibility.Evaluate(account, Snapshot(latest), now, _policy);
        if (latest is not null && IsOpen(latest) && !eligibility.AllowsNewRequest)
        {
            return new AgeVerificationRequestResult.Accepted(ToStatus(latest, eligibility));
        }

        if (!eligibility.AllowsNewRequest)
        {
            return new AgeVerificationRequestResult.NotAllowed();
        }

        if (latest is { State: AgeVerificationState.Pending })
        {
            await ExpireAbandonedAsync(userId, latest, cancellationToken);
        }

        var created = await CreatePendingAsync(userId, loginSessionId, now, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var status = AgeEligibility.Evaluate(account, Snapshot(created), now, _policy);
        return new AgeVerificationRequestResult.Accepted(ToStatus(created, status));
    }

    /// <summary>API-P06: estado da última solicitação da própria pessoa.</summary>
    public async Task<AgeVerificationStatus> GetCurrentAsync(Guid userId, CancellationToken cancellationToken)
    {
        var (latest, eligibility) = await EvaluateAsync(userId, cancellationToken);
        return latest is null
            ? new AgeVerificationStatus(AgeVerificationStatus.NotStarted, eligibility.NextStep, ContinueUrl: null)
            : ToStatus(latest, eligibility);
    }

    /// <summary>
    /// Elegibilidade etária da conta agora (operação pública <c>GetEligibility</c> do Identity, modules.md). A sessão
    /// é recebida e ainda não muda nada: o contrato já a carrega para "verificação a cada acesso", se o parecer
    /// sobre o ECA Digital exigir (ADR-0024, decisão 3).
    /// </summary>
    public async Task<AgeEligibilityResult> GetEligibilityAsync(
        Guid userId,
        Guid loginSessionId,
        CancellationToken cancellationToken)
    {
        _ = loginSessionId;
        var (_, eligibility) = await EvaluateAsync(userId, cancellationToken);
        return eligibility;
    }

    private async Task<(VerificationRow? Latest, AgeEligibilityResult Eligibility)> EvaluateAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        var account = await _db.Users.AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => (AccountStatus?)user.Status)
            .SingleOrDefaultAsync(cancellationToken);
        if (account is null)
        {
            return (null, new AgeEligibilityResult(IsEligible: false, AgeVerificationNextStep.NotAvailable));
        }

        var latest = await FindLatestAsync(userId, cancellationToken);
        return (latest, AgeEligibility.Evaluate(account.Value, Snapshot(latest), _time.GetUtcNow(), _policy));
    }

    private static bool IsOpen(VerificationRow row) =>
        row.State is AgeVerificationState.Pending or AgeVerificationState.ManualReview;

    private async Task<AccountStatus?> LockAccountStatusAsync(Guid userId, CancellationToken cancellationToken)
    {
        var rows = await _db.Database
            .SqlQuery<string>($"select status as \"Value\" from identity.users where id = {userId} for update")
            .ToListAsync(cancellationToken);
        return rows.Count == 1 && Enum.TryParse<AccountStatus>(rows[0], out var status) ? status : null;
    }

    private async Task<VerificationRow?> FindLatestAsync(Guid userId, CancellationToken cancellationToken)
    {
        var rows = await _db.Database.SqlQuery<VerificationRow>(
            $"""
            select id as "Id", state as "StateName", requested_at as "RequestedAt", expires_at as "ExpiresAt",
                   provider_flow_url as "FlowUrl", version as "Version"
            from identity.age_verifications
            where user_id = {userId}
            order by requested_at desc, id desc
            limit 1
            """).ToListAsync(cancellationToken);
        return rows.SingleOrDefault();
    }

    /// <summary>A pendente que passou do prazo vira <c>Expired</c> antes da nova, pelo índice de uma aberta por pessoa.</summary>
    private async Task ExpireAbandonedAsync(Guid userId, VerificationRow abandoned, CancellationToken cancellationToken)
    {
        var expired = nameof(AgeVerificationState.Expired);
        var pending = nameof(AgeVerificationState.Pending);
        var updated = await _db.Database.ExecuteSqlAsync(
            $"""
            update identity.age_verifications
            set state = {expired}, version = version + 1
            where id = {abandoned.Id} and state = {pending}
            """,
            cancellationToken);
        if (updated != 1)
        {
            // A conta está travada nesta transação; só outro caminho de escrita sem a trava chegaria aqui.
            throw new InvalidOperationException("Solicitação pendente mudou sob a trava da conta.");
        }

        await new AuditTrail(_db, _time).AppendAsync(
            new AuditRecord(AuditActorKind.User, userId, AuditAction.AgeVerificationExpired, TargetType, abandoned.Id, abandoned.Version + 1),
            cancellationToken);
    }

    private async Task<VerificationRow> CreatePendingAsync(
        Guid userId,
        Guid loginSessionId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var id = Guid.CreateVersion7(now);
        var nonce = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(NonceBytes));
        var pending = nameof(AgeVerificationState.Pending);
        await _db.Database.ExecuteSqlAsync(
            $"""
            insert into identity.age_verifications
                (id, user_id, state, policy_version, provider, request_nonce, requested_at, version)
            values ({id}, {userId}, {pending}, {_policy.Version}, {_providerName}, {nonce}, {now}, 0)
            """,
            cancellationToken);
        await new AuditTrail(_db, _time).AppendAsync(
            new AuditRecord(AuditActorKind.User, userId, AuditAction.AgeVerificationRequested, TargetType, id, 0),
            cancellationToken);
        _db.Add(AgeVerificationOutbox.Create(
            new AgeVerificationRequested(id, loginSessionId),
            now,
            now + _policy.RequestLifetime));
        return new VerificationRow(id, pending, now, ExpiresAt: null, FlowUrl: null, Version: 0);
    }

    /// <summary>Só uma URL de fluxo https com origem na allowlist vai ao navegador; as demais nem contam como prontas.</summary>
    private Uri? ShowableFlow(VerificationRow? row) =>
        row?.FlowUrl is { } flow && Uri.TryCreate(flow, UriKind.Absolute, out var url) && _options.IsAllowedFlow(url)
            ? url
            : null;

    private AgeVerificationSnapshot? Snapshot(VerificationRow? row) =>
        row is null ? null : new AgeVerificationSnapshot(row.State, row.RequestedAt, row.ExpiresAt, ShowableFlow(row) is not null);

    private AgeVerificationStatus ToStatus(VerificationRow row, AgeEligibilityResult eligibility) =>
        new(
            row.State.ToString(),
            eligibility.NextStep,
            eligibility.NextStep == AgeVerificationNextStep.ContinueWithProvider ? ShowableFlow(row) : null);

    private sealed record VerificationRow(
        Guid Id,
        string StateName,
        DateTimeOffset RequestedAt,
        DateTimeOffset? ExpiresAt,
        string? FlowUrl,
        long Version)
    {
        public AgeVerificationState State => Enum.Parse<AgeVerificationState>(StateName);
    }
}
