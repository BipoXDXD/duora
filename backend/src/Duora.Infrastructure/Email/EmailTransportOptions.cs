using System.Net.Mail;
using Duora.Core.Identity;

namespace Duora.Infrastructure.Email;

internal sealed class EmailTransportOptions
{
    public string Provider { get; set; } = "PickupDirectory";
    public ResendOptions Resend { get; set; } = new();

    public bool IsValid() => Provider == "PickupDirectory" || (Provider == "Resend" && Resend.IsValid());
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
