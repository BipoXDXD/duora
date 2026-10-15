using System.Text;
using Duora.Core.Notifications;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Duora.Infrastructure.Email;

internal sealed class PickupDirectoryOptions
{
    public const string Section = "Email";

    /// <summary>Diretório onde cada e-mail vira um arquivo <c>.eml</c>. Em desenvolvimento, <c>.local/mail/</c>.</summary>
    public string PickupDirectory { get; set; } = "";
}

/// <summary>
/// Adaptador de desenvolvimento: grava cada e-mail num arquivo em vez de enviá-lo. O nome do arquivo é a chave de
/// idempotência, então repetir o envio não cria segunda cópia. O conteúdo (com o link e o token) só vai para o
/// arquivo; o log registra apenas a chave.
/// </summary>
internal sealed partial class PickupDirectoryEmailSender(
    IOptions<PickupDirectoryOptions> options,
    ILogger<PickupDirectoryEmailSender> logger) : ITransactionalEmailSender
{
    public async Task SendAsync(TransactionalEmail email, string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(email);
        // A chave vira nome de arquivo: só hexadecimal, para nunca escapar do diretório.
        if (string.IsNullOrEmpty(idempotencyKey) || !idempotencyKey.All(char.IsAsciiHexDigit))
        {
            throw new ArgumentException("A chave de idempotência deve ser hexadecimal.", nameof(idempotencyKey));
        }

        var directory = Directory.CreateDirectory(options.Value.PickupDirectory);
        var target = Path.Combine(directory.FullName, $"{idempotencyKey}.eml");
        if (File.Exists(target))
        {
            LogAlreadySent(logger, idempotencyKey);
            return;
        }

        // Escreve num temporário e move sem sobrescrever: dois envios simultâneos deixam um arquivo só, inteiro.
        var temporary = Path.Combine(directory.FullName, $"{idempotencyKey}.{Guid.NewGuid():N}.tmp");
        await File.WriteAllTextAsync(temporary, Render(email), Encoding.UTF8, cancellationToken);
        try
        {
            File.Move(temporary, target, overwrite: false);
            LogWritten(logger, idempotencyKey);
        }
        catch (IOException) when (File.Exists(target))
        {
            File.Delete(temporary);
            LogAlreadySent(logger, idempotencyKey);
        }
    }

    private static string Render(TransactionalEmail email) =>
        $"""
        To: {email.To}
        Subject: {email.Subject}
        Content-Type: text/plain; charset=utf-8

        {email.TextBody}

        """;

    [LoggerMessage(Level = LogLevel.Information, Message = "E-mail {IdempotencyKey} gravado no diretório de desenvolvimento")]
    private static partial void LogWritten(ILogger logger, string idempotencyKey);

    [LoggerMessage(Level = LogLevel.Information, Message = "E-mail {IdempotencyKey} já tinha sido gravado; envio repetido ignorado")]
    private static partial void LogAlreadySent(ILogger logger, string idempotencyKey);
}
