using System.Diagnostics.Metrics;

namespace Duora.Web.Identity;

/// <summary>
/// Métricas do login (A04), pelo <see cref="IMeterFactory"/> do host. Sem rótulos: e-mail, IP ou conta dariam
/// cardinalidade sem limite e dado pessoal na métrica.
/// </summary>
internal sealed class LoginMetrics
{
    public const string MeterName = "Duora.Identity";

    /// <summary>Login com e-mail sem conta ou com cadastro não confirmado (os dois casos somam juntos).</summary>
    public const string FailedUnknownAccount = "identity.login.failed_unknown_account";

    private readonly Counter<long> _failedUnknownAccount;

    public LoginMetrics(IMeterFactory meters)
    {
        var meter = meters.Create(MeterName);
        _failedUnknownAccount = meter.CreateCounter<long>(
            FailedUnknownAccount,
            description: "Login com e-mail sem conta ou com cadastro não confirmado.");
    }

    public void RecordFailedUnknownAccount() => _failedUnknownAccount.Add(1);
}
