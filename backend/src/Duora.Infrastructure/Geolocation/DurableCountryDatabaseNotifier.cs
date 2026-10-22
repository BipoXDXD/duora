using System.Globalization;
using System.Net;
using System.Net.Mail;
using System.Text;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Duora.Infrastructure.Geolocation;

public interface ICountryDatabaseMailTransport
{
    Task SendAsync(CountryDatabaseNotification notification, CancellationToken ct);
}

/// <summary>Persist before SMTP; ambiguous attempts never trigger an automatic resend.</summary>
public sealed partial class DurableCountryDatabaseNotifier(NpgsqlDataSource source, ICountryDatabaseMailTransport transport,
    TimeProvider time, ILogger<DurableCountryDatabaseNotifier> logger) : ICountryDatabaseNotifier
{
    public async Task SendAsync(CountryDatabaseNotification notification, CancellationToken ct)
    {
        await using (var c = await source.OpenConnectionAsync(ct))
        {
            await using var q = new NpgsqlCommand("""
                insert into ops.country_database_deliveries(notification_id,attempted_at)
                values($1,$2) on conflict do nothing returning notification_id
                """, c);
            q.Parameters.AddWithValue(notification.Id);
            q.Parameters.AddWithValue(time.GetUtcNow());
            if (await q.ExecuteScalarAsync(ct) is null)
            {
                await using var existing = new NpgsqlCommand("select accepted_at is not null from ops.country_database_deliveries where notification_id=$1", c);
                existing.Parameters.AddWithValue(notification.Id);
                if (await existing.ExecuteScalarAsync(ct) is true)
                { return; }
                LogUncertain(logger, notification.Id);
                throw new CountryDatabaseDeliveryUncertainException();
            }
        }
        // A cancellation or connection error after reservation is deliberately left uncertain.
        // Reconciliation requires checking the exact Message-ID; no retry can guess whether Gmail accepted it.
        await transport.SendAsync(notification, ct);
        await using var accepted = await source.OpenConnectionAsync(ct);
        await using var confirm = new NpgsqlCommand("update ops.country_database_deliveries set accepted_at=$2 where notification_id=$1", accepted);
        confirm.Parameters.AddWithValue(notification.Id);
        confirm.Parameters.AddWithValue(time.GetUtcNow());
        await confirm.ExecuteNonQueryAsync(ct);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Country database notification {NotificationId} needs delivery reconciliation; automatic resend is blocked.")]
    private static partial void LogUncertain(ILogger logger, long notificationId);
}

public sealed class CountryDatabaseDeliveryUncertainException() : Exception("Country database delivery requires reconciliation.");

internal sealed class GmailCountryDatabaseMailTransport(CountryDatabaseGmailOptions options) : ICountryDatabaseMailTransport
{
    internal static string MessageId(CountryDatabaseNotification n) =>
        $"<duora-country-{n.Id.ToString(CultureInfo.InvariantCulture)}-{n.OccurredAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}@meetduora.com>";

    public async Task SendAsync(CountryDatabaseNotification notification, CancellationToken ct)
    {
        using var message = new MailMessage(options.Address, options.Address)
        {
            Subject = notification.Recovered ? "Duora — recuperação da base IPinfo" : "Duora — alerta da base IPinfo",
            Body = string.Create(CultureInfo.InvariantCulture,
                $"Evento operacional {notification.Id}\nCódigo: {notification.Code}\nGravidade: {notification.Severity}\nRecuperado: {notification.Recovered}\nInstante UTC: {notification.OccurredAt:O}\n\nVerifique o estado operacional da base IPinfo no Duora. Esta mensagem não contém dados de participantes."),
            BodyEncoding = Encoding.UTF8,
            SubjectEncoding = Encoding.UTF8,
        };
        message.Headers.Add("Message-ID", MessageId(notification));
        using var smtp = new SmtpClient("smtp.gmail.com", 587)
        {
            EnableSsl = true,
            Credentials = new NetworkCredential(options.Address, options.AppPassword),
            Timeout = 30000,
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await smtp.SendMailAsync(message, timeout.Token);
    }
}
