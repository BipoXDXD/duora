using System.Diagnostics.Metrics;
using Duora.Infrastructure.Identity;

namespace Duora.Web.Http;

/// <summary>
/// Conta ocupada (503) por motivo, pelo <see cref="IMeterFactory"/> do host. O único rótulo é o motivo, de conjunto
/// fechado: conta, e-mail ou rota dariam cardinalidade sem limite e dado pessoal na métrica.
/// </summary>
internal sealed class AccountBusyMetrics
{
    public const string MeterName = "Duora.Identity";

    public const string AccountBusy = "identity.account_busy";

    public const string ReasonTag = "reason";

    private readonly Counter<long> _accountBusy;

    public AccountBusyMetrics(IMeterFactory meters)
    {
        _accountBusy = meters.Create(MeterName).CreateCounter<long>(
            AccountBusy,
            description: "Respostas 503 de conta ocupada, por motivo interno.");
    }

    public void Record(AccountBusyReason reason) =>
        _accountBusy.Add(1, new KeyValuePair<string, object?>(ReasonTag, TagOf(reason)));

    private static string TagOf(AccountBusyReason reason) => reason switch
    {
        AccountBusyReason.AccountLock => "account_lock",
        AccountBusyReason.AccountQueue => "account_queue",
        AccountBusyReason.LoginDeadline => "login_deadline",
        AccountBusyReason.Other => "other",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Motivo de conta ocupada sem rótulo."),
    };
}
