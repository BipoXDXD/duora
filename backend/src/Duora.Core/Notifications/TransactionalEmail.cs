using Duora.Core.Identity;

namespace Duora.Core.Notifications;

/// <summary>E-mail de serviço (conta e segurança), em texto simples, sem conteúdo de terceiros.</summary>
public sealed record TransactionalEmail(EmailAddress To, string Subject, string TextBody);

/// <summary>
/// Porta de envio de e-mail transacional. A chave de idempotência identifica o efeito (a entrega da outbox): um
/// adaptador que a repassa ao provedor, ou a usa para deduplicar, evita duas cópias quando o envio é repetido.
/// </summary>
public interface ITransactionalEmailSender
{
    Task SendAsync(TransactionalEmail email, string idempotencyKey, CancellationToken cancellationToken);
}
