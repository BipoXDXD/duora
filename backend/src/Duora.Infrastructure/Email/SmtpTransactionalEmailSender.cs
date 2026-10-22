using Duora.Core.Notifications;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Duora.Infrastructure.Email;

/// <summary>
/// Sends with mandatory TLS and normal certificate validation. A stable Message-ID helps correlation;
/// SMTP cannot guarantee deduplication after remote acceptance followed by a lost local acknowledgement.
/// </summary>
internal sealed class SmtpTransactionalEmailSender(IOptions<EmailTransportOptions> options) : ITransactionalEmailSender
{
    public async Task SendAsync(TransactionalEmail email, string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(email);
        if (string.IsNullOrEmpty(idempotencyKey) || !idempotencyKey.All(char.IsAsciiHexDigit))
        {
            throw new ArgumentException("A chave de idempotência deve ser hexadecimal.", nameof(idempotencyKey));
        }

        if (email.Subject.Contains('\r', StringComparison.Ordinal) || email.Subject.Contains('\n', StringComparison.Ordinal))
        {
            throw new ArgumentException("O assunto não pode conter quebras de linha.", nameof(email));
        }

        var smtp = options.Value.Smtp;
        var from = MailboxAddress.Parse(smtp.From);
        using var message = new MimeMessage();
        message.From.Add(from);
        message.To.Add(MailboxAddress.Parse(email.To.Value));
        message.Subject = email.Subject;
        message.MessageId = $"duora-{idempotencyKey}@{from.Address[(from.Address.LastIndexOf('@') + 1)..]}";
        message.Body = new TextPart("plain") { Text = email.TextBody };

        // Each delivery owns its connection: batches and lease cancellation never share an SMTP session.
        using var client = new SmtpClient();
        var security = smtp.Security == "StartTls" ? SecureSocketOptions.StartTls : SecureSocketOptions.SslOnConnect;
        try
        {
            await client.ConnectAsync(smtp.Host, smtp.Port, security, cancellationToken);
            await client.AuthenticateAsync(smtp.Username, smtp.Password, cancellationToken);
            await client.SendAsync(message, cancellationToken);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            // MailKit can wrap TLS-handshake cancellation in SslHandshakeException.
            throw new OperationCanceledException(cancellationToken);
        }
        // Dispose closes the connection. A failed QUIT after successful DATA must not trigger another delivery.
    }
}
