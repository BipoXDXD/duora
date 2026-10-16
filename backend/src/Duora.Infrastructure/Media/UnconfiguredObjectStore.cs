using Duora.Core.Media;

namespace Duora.Infrastructure.Media;

/// <summary>
/// Storage do upload desligado (sem <c>Media:Review</c>, ADR-0025): falha fechado em qualquer operação. Nunca é
/// chamado pelo caminho normal, porque a API responde 503 antes; existe para o serviço de upload poder ser montado.
/// </summary>
internal sealed class UnconfiguredObjectStore : IObjectStore
{
    public Task PutAsync(ObjectKey key, Stream content, long length, string contentType, CancellationToken cancellationToken) =>
        throw Unconfigured();

    public Task<Stream?> OpenReadAsync(ObjectKey key, CancellationToken cancellationToken) => throw Unconfigured();

    public Task DeleteAsync(ObjectKey key, CancellationToken cancellationToken) => throw Unconfigured();

    private static InvalidOperationException Unconfigured() =>
        new("Media:Storage:Provider não configurado: o upload de fotos está desligado.");
}
