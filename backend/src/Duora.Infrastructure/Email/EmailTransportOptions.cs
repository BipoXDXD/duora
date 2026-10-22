using System.Net.Mail;
using Duora.Core.Identity;

namespace Duora.Infrastructure.Email;

internal sealed class EmailTransportOptions
{
    public const string Section = "Email";

    public string Provider { get; set; } = "PickupDirectory";
    public ResendOptions Resend { get; set; } = new();
    public SmtpTransportOptions Smtp { get; set; } = new();

    public bool IsValid() => Provider switch
    {
        "PickupDirectory" => true,
        "Resend" => Resend is not null && Resend.IsValid(),
        "Smtp" => Smtp is not null && Smtp.IsValid(),
        _ => false,
    };
}

internal sealed class ResendOptions
{
    public string ApiKey { get; set; } = "";
    public string From { get; set; } = "";

    public bool IsValid() => !string.IsNullOrWhiteSpace(ApiKey)
        && ApiKey.All(c => char.IsAscii(c) && !char.IsControl(c) && !char.IsWhiteSpace(c))
        && !string.IsNullOrWhiteSpace(From) && !From.Any(char.IsControl)
        && MailAddress.TryCreate(From, out var address) && EmailAddress.TryParse(address.Address, out _);
}

internal sealed class SmtpTransportOptions
{
    public string Host { get; set; } = "";

    public int Port { get; set; } = 587;

    public string Security { get; set; } = "StartTls";

    public string Username { get; set; } = "";

    public string Password { get; set; } = "";

    public string From { get; set; } = "";

    public bool IsValid() =>
        !string.IsNullOrWhiteSpace(Host)
        && Uri.CheckHostName(Host) != UriHostNameType.Unknown
        && Port is > 0 and <= 65535
        && Security is "StartTls" or "SslOnConnect"
        && !string.IsNullOrWhiteSpace(Username)
        && !string.IsNullOrWhiteSpace(Password)
        && EmailAddress.TryParse(From, out _);
}
