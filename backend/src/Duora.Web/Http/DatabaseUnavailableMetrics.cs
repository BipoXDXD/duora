using System.Diagnostics.Metrics;

namespace Duora.Web.Http;

/// <summary>
/// Banco fora do alcance (o 503 do evento <c>DatabaseUnavailable</c>) por SqlState, pelo <see cref="IMeterFactory"/>
/// do host, para alertar por taxa (#498). O único rótulo é o SqlState, código de cinco caracteres do servidor, ou
/// <c>-</c> quando o servidor nem respondeu: rota, conta ou mensagem do Npgsql dariam cardinalidade sem limite e
/// host, porta ou usuário na métrica. A contenção fica de fora: passa sozinha e tem Warning próprio.
/// </summary>
internal sealed class DatabaseUnavailableMetrics
{
    public const string MeterName = "Duora.Database";

    public const string Unavailable = "duora.database.unavailable";

    public const string SqlStateTag = "sqlstate";

    private readonly Counter<long> _unavailable;

    public DatabaseUnavailableMetrics(IMeterFactory meters)
    {
        _unavailable = meters.Create(MeterName).CreateCounter<long>(
            Unavailable,
            description: "Respostas 503 de banco indisponível (sem a contenção), por SqlState.");
    }

    public void Record(string sqlState) =>
        _unavailable.Add(1, new KeyValuePair<string, object?>(SqlStateTag, sqlState));
}
