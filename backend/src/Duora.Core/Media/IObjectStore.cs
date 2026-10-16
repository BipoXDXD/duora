namespace Duora.Core.Media;

/// <summary>
/// Porta do object storage privado (ADR-0010, ADR-0025). Sem listagem nem URL: quem lê passa pela API, que
/// revalida a autorização. Toda operação é repetível, porque banco e storage não têm transação comum (spec 16.2):
/// gravar de novo na mesma chave sobrescreve, e apagar o que já não existe é sucesso.
/// </summary>
public interface IObjectStore
{
    /// <summary>
    /// Grava exatamente <paramref name="length"/> bytes lidos de <paramref name="content"/> (que pode não ser
    /// pesquisável). Se o fluxo terminar antes ou passar disso, lança e não deixa objeto legível na chave.
    /// </summary>
    Task PutAsync(ObjectKey key, Stream content, long length, string contentType, CancellationToken cancellationToken);

    /// <summary>Abre o objeto para leitura; <c>null</c> quando ele não existe.</summary>
    Task<Stream?> OpenReadAsync(ObjectKey key, CancellationToken cancellationToken);

    Task DeleteAsync(ObjectKey key, CancellationToken cancellationToken);
}
