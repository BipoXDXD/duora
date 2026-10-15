using System.Collections.Immutable;

namespace Duora.Core.Experiences;

/// <summary>
/// Versões publicadas do conteúdo de uma experiência. Publicar devolve um novo catálogo; uma versão publicada nunca é
/// substituída e as versões só crescem, então uma sessão que fixou a versão X continua lendo X (INV-09).
/// </summary>
public sealed class ContentCatalog<TContent>
    where TContent : class, IExperienceContent
{
    private readonly ImmutableSortedDictionary<int, TContent> _versions;

    /// <summary>Catálogo vazio, sem versão publicada.</summary>
    public ContentCatalog()
        : this(ImmutableSortedDictionary<int, TContent>.Empty)
    {
    }

    private ContentCatalog(ImmutableSortedDictionary<int, TContent> versions) => _versions = versions;

    public TContent Latest => _versions.IsEmpty
        ? throw new InvalidOperationException("Nenhuma versão publicada.")
        : _versions.Values.Last();

    public ContentCatalog<TContent> Publish(TContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!_versions.IsEmpty)
        {
            var latest = Latest;
            if (content.ExperienceId != latest.ExperienceId)
            {
                throw new ArgumentException("O catálogo guarda uma única experiência.", nameof(content));
            }

            if (content.ContentVersion <= latest.ContentVersion)
            {
                throw new ArgumentException(
                    $"A versão {content.ContentVersion} não é maior que a última publicada ({latest.ContentVersion}).",
                    nameof(content));
            }
        }

        return new ContentCatalog<TContent>(_versions.Add(content.ContentVersion, content));
    }

    public TContent? Find(int contentVersion) => _versions.GetValueOrDefault(contentVersion);
}
