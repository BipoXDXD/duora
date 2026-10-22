using Duora.Core.Identity;

namespace Duora.Infrastructure.Email;

internal sealed class EmailTransportOptions
{
    public const string Section = "Email";

    public string Transport { get; set; } = "Pickup";

    public SmtpTransportOptions Smtp { get; set; } = new();

    public bool UsesSmtp => string.Equals(Transport, "Smtp", StringComparison.OrdinalIgnoreCase);

    public bool IsValid() =>
        UsesSmtp
            ? Smtp is not null && Smtp.IsValid()
            : string.Equals(Transport, "Pickup", StringComparison.OrdinalIgnoreCase);
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
