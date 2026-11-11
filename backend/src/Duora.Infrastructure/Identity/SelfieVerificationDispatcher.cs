using System.Text.Json;
using Duora.Core.Identity;
using Duora.Infrastructure.Audit;
using Duora.Infrastructure.Outbox;
using Duora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Duora.Infrastructure.Identity;

/// <summary>Outbox com teto, renovação e fencing; chamadas externas sempre fora de transações.</summary>
public sealed partial class SelfieVerificationDispatcher
{
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Corte de cada chamada ao fornecedor, no relógio injetado. Fica bem abaixo do lease, para a resposta ainda ser
    /// gravada pelo dono da entrega.
    /// </summary>
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);

    private const string AnswerAfterDeadline = "Selfie provider answered after the call deadline.";
    private const string CallCut = "Selfie provider call cut at the deadline.";
    private static readonly TimeSpan TransientDataRetention = TimeSpan.FromHours(24);

    /// <summary>
    /// Prazo mínimo para abrir sessão: menos que isso gera um Capturing que a pessoa não consegue usar. HIPÓTESE, não
    /// medida: nem o repositório nem o SDK trazem a duração real do início à captura, que inclui passos humanos e a
    /// leitura do resultado antes do prazo. Calibrar com medição antes de tratar o valor como requisito.
    /// </summary>
    private static readonly TimeSpan MinimumCaptureWindow = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Decisões do dono em 28/10/2026 (issue #93): tentativa não aprovada sai 30 dias depois; o Passed fica enquanto a
    /// conta existir e mantém a participação.
    /// </summary>
    private static readonly TimeSpan UnapprovedRetention = TimeSpan.FromDays(30);
    private readonly DuoraDbContext _db;
    private readonly OutboxDeliveryQueue _queue;
    private readonly ISelfieProvider _provider;
    private readonly SelfieProviderCallSlot _slot;
    private readonly SelfieProviderOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<SelfieVerificationDispatcher> _logger;

    /// <summary>Chamada deste lote que o corte abandonou ainda em andamento; ela leva a vaga consigo.</summary>
    private Task? _abandonedCall;

    internal SelfieVerificationDispatcher(DuoraDbContext db, OutboxDeliveryQueue queue, ISelfieProvider provider,
        SelfieProviderCallSlot slot, IOptions<SelfieProviderOptions> options, TimeProvider time,
        ILogger<SelfieVerificationDispatcher> logger)
    {
        _db = db;
        _queue = queue;
        _provider = provider;
        _slot = slot;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    public async Task<int> DispatchBatchAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        await PurgeExpiredDataAsync(now, cancellationToken);

        if (!_options.Enabled)
        {
            return 0;
        }

        // Sem a vaga, nada é tomado da fila: uma entrega reivindicada sem poder chamar o fornecedor só gastaria tentativa.
        if (!_slot.TryAcquire(out var firstRefusal))
        {
            if (firstRefusal)
            {
                LogPreviousCallStillRunning(_logger);
            }

            return 0;
        }

        _abandonedCall = null;
        try
        {
            return await DispatchClaimsAsync(now, cancellationToken);
        }
        finally
        {
            _slot.Release(_abandonedCall);
        }
    }

    private async Task<int> DispatchClaimsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Lote de 1 de propósito: a vaga e _abandonedCall cobrem uma única chamada por lote. Num lote maior, a entrega
        // seguinte chamaria o fornecedor com a anterior ainda rodando.
        var claims = await _queue.ClaimWithAttemptLimitAsync(SelfieVerificationOutbox.Consumer, 1, now, now + Lease, 40, cancellationToken);
        foreach (var exhausted in claims.Exhausted)
        {
            if (Guid.TryParse(exhausted.RecipientKey, out var id))
            {
                await _db.Set<SelfieVerificationRecord>().Where(v => v.Id == id
                    && (v.State == SelfieState.Pending || v.State == SelfieState.Capturing || v.State == SelfieState.Processing))
                    .ExecuteUpdateAsync(s => s.SetProperty(v => v.State, SelfieState.Expired), cancellationToken);
            }
        }

        foreach (var delivery in claims.Claimed)
        {
            await ProcessAsync(delivery, cancellationToken);
        }

        return claims.Claimed.Count;
    }

    /// <summary>
    /// Roda mesmo com a integração desligada. Não há imagens no banco: a referência da sessão e o feedback saem 24h
    /// após a expiração; a tentativa não aprovada sai 30 dias após a conclusão (ou a expiração, se nunca concluiu). O
    /// Passed fica: sai só com a conta (FK em cascata).
    /// </summary>
    private async Task PurgeExpiredDataAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var transientCutoff = now - TransientDataRetention;
        await _db.Set<SelfieVerificationRecord>().Where(v => v.ExpiresAt < transientCutoff
                && (v.ProviderReference != null || v.FeedbackCode != null))
            .ExecuteUpdateAsync(s => s.SetProperty(v => v.ProviderReference, (string?)null)
                .SetProperty(v => v.FeedbackCode, (string?)null), cancellationToken);

        var unapprovedCutoff = now - UnapprovedRetention;
        await _db.Set<SelfieVerificationRecord>().Where(v => v.State != SelfieState.Passed
                && ((v.CompletedAt != null && v.CompletedAt <= unapprovedCutoff)
                    || (v.CompletedAt == null && v.ExpiresAt <= unapprovedCutoff)))
            .ExecuteDeleteAsync(cancellationToken);
    }

    private async Task ProcessAsync(ClaimedDelivery delivery, CancellationToken cancellationToken)
    {
        Guid id;
        try
        {
            id = JsonSerializer.Deserialize<Guid>(delivery.Payload);
        }
        catch (JsonException)
        {
            await _queue.SettleAsync(delivery, OutboxDeliveryStatus.Failed, "InvalidPayload", _time.GetUtcNow(), cancellationToken);
            return;
        }

        var row = await _db.Set<SelfieVerificationRecord>().AsNoTracking().SingleOrDefaultAsync(v => v.Id == id, cancellationToken);
        if (delivery.EventType != SelfieVerificationOutbox.EventType || row is null
            || row.State is SelfieState.Passed or SelfieState.RecaptureRequired or SelfieState.Inconclusive or SelfieState.Expired)
        {
            await _queue.SettleAsync(delivery, OutboxDeliveryStatus.Delivered, null, _time.GetUtcNow(), cancellationToken);
            return;
        }

        if (row.ExpiresAt <= _time.GetUtcNow())
        {
            await ExpireAsync(row, delivery, null, cancellationToken);
            return;
        }

        if (row.ProviderReference is null && row.ExpiresAt - _time.GetUtcNow() < MinimumCaptureWindow)
        {
            LogNoTimeToCapture(_logger);
            await ExpireAsync(row, delivery, SelfieFeedbackCodes.ProviderUnavailable, cancellationToken);
            return;
        }

        if (!(await _queue.RenewLeasesAsync([delivery], Lease, _time, cancellationToken)).Contains(delivery.Id))
        {
            return;
        }

        SelfieSession? session = null;
        SelfieResult? result = null;
        try
        {
            var reference = row.ProviderReference;
            if (reference is null)
            {
                session = await CallProviderAsync(token => _provider.StartAsync(row.Id, token), cancellationToken);
            }
            else
            {
                result = await CallProviderAsync(token => _provider.ReadAsync(reference, token), cancellationToken);
            }
        }
        catch (Exception exception) when (IsTransientProviderFailure(exception))
        {
            cancellationToken.ThrowIfCancellationRequested();
            LogProviderUnavailable(_logger, exception.GetType().Name);
            await SignalProviderUnavailableAsync(row, delivery, cancellationToken);
            await _queue.RescheduleAsync(delivery, _time.GetUtcNow().AddSeconds(5), "ProviderUnavailable", _time.GetUtcNow(), cancellationToken);
            return;
        }

        var applied = await ApplyAsync(row, delivery, session, result, cancellationToken);
        if (!applied)
        {
            return;
        }

        if (result is { State: not (SelfieState.Processing or SelfieState.Capturing or SelfieState.Pending) })
        {
            await _queue.SettleAsync(delivery, OutboxDeliveryStatus.Delivered, null, _time.GetUtcNow(), cancellationToken);
        }
        else
        {
            await _queue.RescheduleAsync(delivery, _time.GetUtcNow().AddSeconds(5), "AwaitingCapture", _time.GetUtcNow(), cancellationToken);
        }
    }

    /// <summary>
    /// O corte em duas camadas, no relógio injetado, as duas presas a um único timer armado antes da chamada: o prazo fica
    /// fixo quando a chamada sai, e um salto do relógio enquanto a espera é montada não o adia. O timer cancela o token no
    /// prazo mesmo que a chamada ainda não tenha devolvido a tarefa (o SDK da AWS lê o corpo da resposta de forma
    /// síncrona). A espera pelo mesmo token devolve o controle mesmo que o fornecedor ignore o cancelamento. A chamada
    /// que segue rodando fica abandonada: ninguém lê a resposta dela, e ela leva a vaga até terminar. Um bloqueio
    /// síncrono que também ignore o token não é interrompido: a espera só age sobre a tarefa devolvida, e a recebe pronta
    /// mesmo depois do prazo. Por isso a resposta que volta depois do prazo é recusada aqui, com o mesmo efeito do corte.
    /// </summary>
    private async Task<T> CallProviderAsync<T>(Func<CancellationToken, Task<T>> call, CancellationToken cancellationToken)
    {
        var deadline = _time.GetUtcNow() + CallTimeout;
        using var cut = new CancellationTokenSource(CallTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cut.Token);
        var pending = call(linked.Token);
        try
        {
            var answer = await WaitWithinTheCutAsync(pending, linked.Token, cancellationToken);
            return _time.GetUtcNow() < deadline ? answer : throw new TimeoutException(AnswerAfterDeadline);
        }
        finally
        {
            if (!pending.IsCompleted)
            {
                // A espera pode vencer o cancelamento do fornecedor: sem cancelar aqui, o token seria descartado sem
                // avisar o fornecedor. A vaga vem antes, para uma exceção em callback do token não a devolver com a
                // chamada viva; se o cancelamento encerrar a chamada, a vaga trata a tarefa concluída.
                _abandonedCall = pending;
                await linked.CancelAsync();
            }
        }
    }

    /// <summary>
    /// Quem chama vê o mesmo de antes: o desfecho do próprio fornecedor quando ele terminou, a
    /// <see cref="TimeoutException"/> quando o corte encerrou a espera com a chamada ainda viva, e o cancelamento do
    /// worker como cancelamento.
    /// </summary>
    private static async Task<T> WaitWithinTheCutAsync<T>(Task<T> pending, CancellationToken cutOrShutdown,
        CancellationToken shutdown)
    {
        try
        {
            return await pending.WaitAsync(cutOrShutdown);
        }
        catch (OperationCanceledException) when (!shutdown.IsCancellationRequested)
        {
            return pending.IsCompleted ? await pending : throw new TimeoutException(CallCut);
        }
    }

    private async Task ExpireAsync(SelfieVerificationRecord row, ClaimedDelivery delivery, string? feedbackCode,
        CancellationToken cancellationToken)
    {
        await ApplyAsync(row, delivery, null, new(SelfieState.Expired, feedbackCode, row.PolicyVersion), cancellationToken);
        await _queue.SettleAsync(delivery, OutboxDeliveryStatus.Expired, null, _time.GetUtcNow(), cancellationToken);
    }

    /// <summary>Só o tipo: a mensagem de erro do fornecedor pode carregar dado (spec 19.2).</summary>
    [LoggerMessage(Level = LogLevel.Warning, Message = "Selfie provider unavailable ({Cause}); the delivery was rescheduled.")]
    private static partial void LogProviderUnavailable(ILogger logger, string cause);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A selfie provider call cut by the timeout is still running; this worker makes no new call until it ends.")]
    private static partial void LogPreviousCallStillRunning(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Selfie attempt expired before opening a session: too little time left to capture.")]
    private static partial void LogNoTimeToCapture(ILogger logger);

    /// <summary>
    /// Falhas da chamada ao fornecedor que reagendam a entrega, sem mudar o estado da tentativa nem contar reprovação. O SDK da
    /// AWS entrega o reset de conexão como <see cref="IOException"/> e o timeout do HttpClient como
    /// <see cref="TimeoutException"/>, a mesma exceção do corte deste dispatcher. Qualquer outra exceção é defeito: sobe ao loop do worker e o lease devolve a entrega.
    /// </summary>
    private static bool IsTransientProviderFailure(Exception exception) =>
        exception is Amazon.Runtime.AmazonServiceException or HttpRequestException or IOException or TimeoutException
            or InvalidDataException or OperationCanceledException;

    /// <summary>
    /// Grava o sinal na tentativa aberta para a tela diferenciar a instabilidade da espera normal. Com lease e versão
    /// conferidos, um worker que perdeu a entrega não escreve; e só a primeira falha da sequência grava.
    /// </summary>
    private async Task SignalProviderUnavailableAsync(SelfieVerificationRecord previous, ClaimedDelivery delivery,
        CancellationToken cancellationToken)
    {
        if (previous.FeedbackCode == SelfieFeedbackCodes.ProviderUnavailable)
        {
            return;
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        if (!await OwnsDeliveryAsync(delivery, _time.GetUtcNow(), cancellationToken))
        {
            return;
        }

        await _db.Set<SelfieVerificationRecord>().Where(v => v.Id == previous.Id && v.Version == previous.Version
                && (v.State == SelfieState.Pending || v.State == SelfieState.Capturing || v.State == SelfieState.Processing))
            .ExecuteUpdateAsync(set => set.SetProperty(v => v.FeedbackCode, SelfieFeedbackCodes.ProviderUnavailable)
                .SetProperty(v => v.Version, v => v.Version + 1), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Trava a entrega até o fim da transação: falso quando o lease venceu ou outro worker a assumiu.</summary>
    private async Task<bool> OwnsDeliveryAsync(ClaimedDelivery delivery, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var owned = await _db.Database.SqlQuery<int>($"""
            select 1 as "Value" from ops.outbox_deliveries
            where id = {delivery.Id} and fencing_token = {delivery.FencingToken}
                and status = 'InFlight' and lease_expires_at > {now} for update
            """).ToListAsync(cancellationToken);
        return owned.Count == 1;
    }

    /// <summary>
    /// Na transação do resultado, sob a trava da conta (issue #300): grava a reprovação e, se ela é a décima, o pedido
    /// de suspensão na outbox. A suspensão em si roda depois, fora desta transação.
    /// </summary>
    private async Task RecordFailureAsync(SelfieVerificationRecord record, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!await SelfieFailureLedger.RecordAsync(_db, record.Id, record.UserId, now, cancellationToken))
        {
            return;
        }

        var failures = await SelfieFailureLedger.ReadAsync(_db, record.UserId, cancellationToken);
        if (SelfieAttemptPolicy.ExhaustsAttempts(failures.Count))
        {
            _db.Add(SelfieAttemptsSuspensionOutbox.Create(record.UserId, record.Id, now));
        }
    }

    private async Task<bool> ApplyAsync(SelfieVerificationRecord previous, ClaimedDelivery delivery,
        SelfieSession? session, SelfieResult? result, CancellationToken cancellationToken)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        var now = _time.GetUtcNow();
        if (!await OwnsDeliveryAsync(delivery, now, cancellationToken))
        {
            return false;
        }

        var accounts = await _db.Database.SqlQuery<string>($"""
            select status as "Value" from identity.users where id = {previous.UserId} and email_confirmed for update
            """).ToListAsync(cancellationToken);
        var allowed = accounts.Count == 1 && accounts[0] is "Active" or "PendingEligibility";
        var record = await _db.Set<SelfieVerificationRecord>().SingleAsync(v => v.Id == previous.Id, cancellationToken);
        if (record.Version != previous.Version || record.State is SelfieState.Passed or SelfieState.Expired
            or SelfieState.Inconclusive or SelfieState.RecaptureRequired)
        {
            return false;
        }

        AuditAction? decision = null;
        if (!allowed || record.ExpiresAt <= now)
        {
            record.State = SelfieState.Expired;
        }
        else if (session is not null)
        {
            record.ProviderReference = session.Reference;
            record.FeedbackCode = null;
            record.ExpiresAt = session.ExpiresAt < record.ExpiresAt ? session.ExpiresAt : record.ExpiresAt;
            record.State = SelfieState.Capturing;
        }
        else if (result is not null)
        {
            record.State = result.PolicyVersion != record.PolicyVersion ? SelfieState.Inconclusive
                : result.State == SelfieState.Processing ? SelfieState.Capturing : result.State;
            record.FeedbackCode = result.FeedbackCode;
            if (record.State == SelfieState.Passed)
            {
                var activated = await _db.Database.ExecuteSqlAsync($"""
                    update identity.users set status = 'Active', concurrency_stamp = {Guid.NewGuid().ToString()}
                    where id = {record.UserId} and status = 'PendingEligibility' and email_confirmed
                    """, cancellationToken);
                await SelfieFailureLedger.ClearAsync(_db, record.UserId, cancellationToken);
                if (activated == 1)
                {
                    await AuditAsync(AuditAction.AccountActivated, AuditTargetTypes.User, record.UserId, null, cancellationToken);
                }

                decision = AuditAction.SelfieVerificationPassed;
            }
            else if (SelfieAttemptPolicy.CountsAsFailure(result, record.PolicyVersion))
            {
                await RecordFailureAsync(record, now, cancellationToken);
                decision = AuditAction.SelfieVerificationFailed;
            }
        }

        record.Version++;
        if (decision is not null)
        {
            await AuditAsync(decision, AuditTargetTypes.SelfieVerification, record.Id, record.Version, cancellationToken);
        }

        if (record.State is SelfieState.Passed or SelfieState.RecaptureRequired or SelfieState.Inconclusive or SelfieState.Expired)
        {
            record.CompletedAt = now;
        }

        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        _db.ChangeTracker.Clear();
        return true;
    }

    /// <summary>
    /// Na transação do resultado (TM-92): só ator, ação e alvo, sem estado do fornecedor, código de feedback, imagem nem
    /// score. O ator é o sistema, porque quem decide é o resultado lido do fornecedor, não uma pessoa.
    /// </summary>
    private Task AuditAsync(AuditAction action, string targetType, Guid targetId, long? targetVersion,
        CancellationToken cancellationToken) =>
        new AuditTrail(_db, _time).AppendAsync(
            new AuditRecord(AuditActorKind.System, actorId: null, action, targetType, targetId, targetVersion),
            cancellationToken);
}
