namespace Duora.IntegrationTests;

/// <summary>Raiz do repositório (onde fica o <c>global.json</c>), para ler arquivos de infraestrutura versionados.</summary>
internal static class RepositoryRoot
{
    public static string Find()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "global.json")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException($"global.json não encontrado acima de {AppContext.BaseDirectory}.");
    }
}
