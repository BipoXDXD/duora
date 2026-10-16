using System.Text.Json;
using Duora.Core.Identity;
using Duora.Infrastructure.Audit;
using Duora.Infrastructure.Operations;
using Duora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Duora.Infrastructure.Identity;

/// <summary>Resultado do processamento de um callback etário.</summary>
public enum AgeWebhookOutcome
{
    /// <summary>Autêntico e registrado: aplicado, repetido ou sem efeito (referência, nonce ou estado não casam).</summary>
    Accepted,

    /// <summary>Assinatura ausente, inválida ou fora da janela de ±5 minutos. Nada foi gravado.</summary>
    InvalidSignature,

    /// <summary>Assinatura válida, mas corpo fora do schema. Nada foi gravado.</summary>
    InvalidPayload,
}

/// <summary>
/// Webhook do fornecedor etário processado inline (ADR-0024, decisão 2): a assinatura é conferida sobre os bytes
/// crus antes de qualquer parse; numa transação, o recibo único é gravado com <c>ON CONFLICT DO NOTHING</c>, a conta
/// é travada (mesma ordem de API-P05: conta, depois solicitação), a solicitação muda com UPDATE condicional
/// <c>WHERE state = 'Pending'</c>, a conta segue <see cref="AgeEligibility.Decide"/> e tudo é auditado.
/// </summary>
public sealed class AgeVerificationWebhook
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly DuoraDbContext _db;
    private readonly AgeVerificationOptions _options;
    private readonly AgeVerificationPolicy _policy;
    private readonly string _providerName;
    private readonly TimeProvider _time;

    internal AgeVerificationWebhook(DuoraDbContext db, AgeVerificationOptions options, string providerName, TimeProvider time)
    {
        _db = db;
        _options = options;
        _policy = options.ToPolicy();
        _providerName = providerName;
        _time = time;
    }

    /// <summary>Header da assinatura do fornecedor configurado.</summary>
    public const string SignatureHeader = FakeAgeCallbackProtocol.SignatureHeader;

    public const int MaxBodyBytes = FakeAgeCallbackProtocol.MaxBodyBytes;

    /// <summary>Só o fornecedor configurado tem webhook; qualquer outro nome (inclusive sem fornecedor) é 404.</summary>
    public bool Handles(string provider) =>
        _options.Provider == AgeVerificationProviderKind.Fake && string.Equals(provider, _providerName, StringComparison.Ordinal);

    public async Task<AgeWebhookOutcome> ProcessAsync(byte[] body, string? signature, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        var now = _time.GetUtcNow();
        if (!FakeAgeCallbackProtocol.IsAuthentic(body, signature, _options.Fake.SigningSecrets, now))
        {
            return AgeWebhookOutcome.InvalidSignature;
        }

        if (!FakeAgeCallbackProtocol.TryReadBody(body, out var callback) || callback is null)
        {
            return AgeWebhookOutcome.InvalidPayload;
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        var receiptId = await WebhookReceipts.TryRecordAsync(
            _db,
            _providerName,
            callback.EventId,
            body,
            MinimalPayload(callback),
            now,
            cancellationToken);
        if (receiptId is null)
        {
            // Mesmo evento já recebido: o efeito, se houve, já foi aplicado uma vez.
            return AgeWebhookOutcome.Accepted;
        }

        var applied = await ApplyAsync(callback, now, cancellationToken);
        await WebhookReceipts.SettleAsync(
            _db,
            receiptId.Value,
            applied ? WebhookReceiptState.Processed : WebhookReceiptState.Rejected,
            now,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return AgeWebhookOutcome.Accepted;
    }

    /// <summary>Recibo guarda só o que foi validado, sem o nonce (P3; spec 16.4).</summary>
    private static string MinimalPayload(AgeCallback callback) =>
        JsonSerializer.Serialize(
            new { type = FakeAgeCallbackProtocol.CompletedType, reference = callback.ProviderReference, outcome = callback.Outcome.ToString() },
            Json);

    private async Task<bool> ApplyAsync(AgeCallback callback, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Associação a uma solicitação criada pelo servidor: fornecedor, referência e nonce precisam casar (TM-36).
        var match = await _db.Database.SqlQuery<VerificationOwner>(
            $"""
            select id as "Id", user_id as "UserId" from identity.age_verifications
            where provider = {_providerName} and provider_reference = {callback.ProviderReference}
              and request_nonce = {callback.RequestNonce}
            """).ToListAsync(cancellationToken);
        if (match.Count != 1)
        {
            return false;
        }

        var (verificationId, userId) = (match[0].Id, match[0].UserId);
        var accounts = await _db.Database
            .SqlQuery<string>($"select status as \"Value\" from identity.users where id = {userId} for update")
            .ToListAsync(cancellationToken);
        var locked = await _db.Database.SqlQuery<LockedVerification>(
            $"""
            select state as "StateName", version as "Version" from identity.age_verifications
            where id = {verificationId} for update
            """).ToListAsync(cancellationToken);
        var account = Enum.Parse<AccountStatus>(accounts.Single());
        var verification = locked.Single();
        if (AgeEligibility.Decide(account, verification.State, callback.Outcome, now, _policy) is not { } decision)
        {
            return false;
        }

        // NotAdult numa solicitação já expirada só restringe a conta; a solicitação fica como estava (M8).
        if (decision.VerificationState != verification.State)
        {
            await RecordDecisionAsync(verificationId, verification.Version, decision, now, cancellationToken);
        }

        if (decision.ChangesAccountFrom(account))
        {
            await MoveAccountAsync(userId, account, decision.AccountStatus, cancellationToken);
        }

        return true;
    }

    private async Task RecordDecisionAsync(
        Guid verificationId,
        long version,
        AgeVerificationDecision decision,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var state = decision.VerificationState.ToString();
        var source = nameof(AgeDecisionSource.Callback);
        var pending = nameof(AgeVerificationState.Pending);
        var updated = await _db.Database.ExecuteSqlAsync(
            $"""
            update identity.age_verifications
            set state = {state}, decided_at = {now}, expires_at = {decision.ExpiresAt}, decision_source = {source},
                version = version + 1
            where id = {verificationId} and state = {pending}
            """,
            cancellationToken);
        if (updated != 1)
        {
            throw new InvalidOperationException("Solicitação mudou sob a trava da conta e da própria linha.");
        }

        await Audit(AuditAction.AgeVerificationDecided, AuditTargetTypes.AgeVerification, verificationId, version + 1, cancellationToken);
    }

    private async Task MoveAccountAsync(Guid userId, AccountStatus from, AccountStatus to, CancellationToken cancellationToken)
    {
        var fromName = from.ToString();
        var toName = to.ToString();
        var updated = await _db.Database.ExecuteSqlAsync(
            $"""
            update identity.users set status = {toName}, concurrency_stamp = {Guid.NewGuid().ToString("D")}
            where id = {userId} and status = {fromName}
            """,
            cancellationToken);
        if (updated != 1)
        {
            throw new InvalidOperationException("Conta mudou sob a própria trava.");
        }

        var action = to switch
        {
            AccountStatus.Active => AuditAction.AccountActivated,
            AccountStatus.Restricted => AuditAction.AccountRestricted,
            _ => throw new InvalidOperationException($"Transição etária sem ação de auditoria: {from} → {to}."),
        };
        await Audit(action, AuditTargetTypes.User, userId, targetVersion: null, cancellationToken);
    }

    private Task Audit(AuditAction action, string targetType, Guid targetId, long? targetVersion, CancellationToken cancellationToken) =>
        new AuditTrail(_db, _time).AppendAsync(
            new AuditRecord(AuditActorKind.System, actorId: null, action, targetType, targetId, targetVersion),
            cancellationToken);

    private sealed record VerificationOwner(Guid Id, Guid UserId);

    private sealed record LockedVerification(string StateName, long Version)
    {
        public AgeVerificationState State => Enum.Parse<AgeVerificationState>(StateName);
    }
}
