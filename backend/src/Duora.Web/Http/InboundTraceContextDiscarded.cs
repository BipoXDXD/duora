using System.Diagnostics;

namespace Duora.Web.Http;

/// <summary>
/// O host não adota o <c>traceparent</c>, o <c>tracestate</c> nem o <c>baggage</c> de quem chama: o trace id vira o
/// <c>correlation_id</c> da auditoria e o <c>traceId</c> dos erros, e um cliente não pode escolhê-lo (colar o pedido
/// dele num trace alheio ou repetir o id de outro registro). Cada pedido começa um trace novo. O que a aplicação
/// manda para fora continua no formato W3C padrão.
/// </summary>
internal sealed class InboundTraceContextDiscarded : DistributedContextPropagator
{
    private static readonly DistributedContextPropagator Outbound = CreateDefaultPropagator();

    public override IReadOnlyCollection<string> Fields => Outbound.Fields;

    public override void Inject(Activity? activity, object? carrier, PropagatorSetterCallback? setter) =>
        Outbound.Inject(activity, carrier, setter);

    public override void ExtractTraceIdAndState(
        object? carrier,
        PropagatorGetterCallback? getter,
        out string? traceId,
        out string? traceState)
    {
        traceId = null;
        traceState = null;
    }

    public override IEnumerable<KeyValuePair<string, string?>>? ExtractBaggage(
        object? carrier,
        PropagatorGetterCallback? getter) => null;
}
