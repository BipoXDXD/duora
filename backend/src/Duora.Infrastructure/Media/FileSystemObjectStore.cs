using Duora.Core.Media;

namespace Duora.Infrastructure.Media;

/// <summary>
/// Object storage em diretório local. Em <c>Production</c> cada área é um diretório do host montado no container
/// (adendo do ADR-0010), com o marcador <see cref="AreaMarkerFileName"/> gravado pelo Ansible; o boot recusa área sem
/// ele e o adaptador nunca cria diretório, porque o que ele criasse estaria no disco efêmero do container (spec 15.4).
/// Grava num arquivo temporário e renomeia, para uma escrita interrompida nunca aparecer como objeto.
/// </summary>
internal sealed class FileSystemObjectStore : IObjectStore
{
    /// <summary>Arquivo que o role <c>app_dirs</c> grava em cada área do host: só existe se a montagem existir.</summary>
    public const string AreaMarkerFileName = ".duora-media-area";

    private const string TemporarySuffix = ".partial";

    private readonly string _root;
    private readonly bool _createMissingDirectories;

    public FileSystemObjectStore(string rootDirectory, bool createMissingDirectories = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
        _createMissingDirectories = createMissingDirectories;
    }

    /// <summary>
    /// Montagens obrigatórias do pipeline legado. A montagem de review-candidates será validada separadamente
    /// quando HumanReview estiver implementado; reconhecer suas chaves não habilita essa política.
    /// </summary>
    public static IReadOnlyList<string> AreaDirectories { get; } = ["quarantine", "derivatives"];

    /// <summary>Verdadeiro se cada área da raiz tem o marcador da montagem.</summary>
    public static bool HasMountedAreas(string rootDirectory) =>
        AreaDirectories.All(area => File.Exists(Path.Combine(rootDirectory, area, AreaMarkerFileName)));

    public async Task PutAsync(ObjectKey key, Stream content, long length, string contentType, CancellationToken cancellationToken)
    {
        var path = PathOf(key);
        var directory = Path.GetDirectoryName(path)!;
        if (_createMissingDirectories)
        {
            Directory.CreateDirectory(directory);
        }
        else if (!Directory.Exists(directory))
        {
            throw new InvalidOperationException("Área de mídia ausente: a montagem do diretório do host sumiu.");
        }

        var temporary = $"{path}.{Guid.NewGuid():N}{TemporarySuffix}";
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await using var exact = new ExactLengthStream(content, length);
                await exact.CopyToAsync(file, cancellationToken);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    public Task<Stream?> OpenReadAsync(ObjectKey key, CancellationToken cancellationToken)
    {
        try
        {
            Stream stream = new FileStream(PathOf(key), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            return Task.FromResult<Stream?>(stream);
        }
        catch (FileNotFoundException)
        {
            return Task.FromResult<Stream?>(null);
        }
        catch (DirectoryNotFoundException)
        {
            return Task.FromResult<Stream?>(null);
        }
    }

    public Task DeleteAsync(ObjectKey key, CancellationToken cancellationToken)
    {
        try
        {
            File.Delete(PathOf(key));
        }
        catch (DirectoryNotFoundException)
        {
            // Sem o diretório da área também não há o objeto: é o estado pedido.
        }

        return Task.CompletedTask;
    }

    /// <summary>A chave já tem formato fechado (<see cref="ObjectKey"/>); a checagem do prefixo é a segunda trava.</summary>
    private string PathOf(ObjectKey key)
    {
        var path = Path.GetFullPath(Path.Combine(_root, key.Value));
        if (!path.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Chave de objeto fora do diretório de mídia.");
        }

        return path;
    }
}
