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
        _accountBusy = meters.Create(MeterName).CreateCounter<long>(AccountBusy);
    }

    // Esqueleto para o teste compilar; a contagem por motivo vem no commit seguinte.
    public void Record(AccountBusyReason reason)
    {
        _ = (_accountBusy, reason);
    }
}
