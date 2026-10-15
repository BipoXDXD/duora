using System.Buffers;
using System.Text;

namespace Duora.Web.Identity;

/// <summary>
/// Diz se o diretório do key ring é o ponto de montagem de um volume (spec 11.2). Sem o volume, o diretório existe
/// do mesmo jeito (a imagem o cria) e o Data Protection grava nele sem erro, mas na camada gravável do container: a
/// chave some no próximo deploy e todas as sessões caem. Só a tabela de montagens do processo distingue os casos.
/// </summary>
internal static class KeyRingMount
{
    public const string MountInfoPath = "/proc/self/mountinfo";

    // A camada do container e a memória somem com ele; qualquer outro sistema de arquivos vem de volume ou bind.
    private static readonly HashSet<string> EphemeralFilesystems = new(StringComparer.Ordinal) { "overlay", "tmpfs", "ramfs" };

    private const int MountPointField = 4;
    private const string OptionalFieldsEnd = "-";
    private const int OctalDigits = 3;
    private static readonly SearchValues<char> OctalDigitChars = SearchValues.Create("01234567");

    /// <summary>Lê a tabela de montagens do processo; fora do Linux ela não existe e nada conta como volume.</summary>
    public static bool IsPersistentMountPoint(string directory) =>
        File.Exists(MountInfoPath) && IsPersistentMountPoint(directory, File.ReadLines(MountInfoPath));

    /// <summary>
    /// <paramref name="mountInfo"/> segue o formato de <c>/proc/self/mountinfo</c> (proc(5)). Vale a última montagem
    /// sobre o mesmo ponto, porque ela esconde as anteriores.
    /// </summary>
    public static bool IsPersistentMountPoint(string directory, IEnumerable<string> mountInfo)
    {
        var target = Normalize(directory);
        string? filesystem = null;
        foreach (var line in mountInfo)
        {
            if (TryParse(line, out var mountPoint, out var type) && mountPoint == target)
            {
                filesystem = type;
            }
        }

        return filesystem is not null && !EphemeralFilesystems.Contains(filesystem);
    }

    private static bool TryParse(string line, out string mountPoint, out string filesystem)
    {
        mountPoint = "";
        filesystem = "";
        var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var separator = Array.IndexOf(fields, OptionalFieldsEnd);
        if (fields.Length <= MountPointField || separator <= MountPointField || separator + 1 >= fields.Length)
        {
            return false;
        }

        mountPoint = Normalize(Unescape(fields[MountPointField]));
        filesystem = fields[separator + 1];
        return true;
    }

    private static string Normalize(string path) =>
        path.Length > 1 ? path.TrimEnd('/') : path;

    /// <summary>O kernel escreve espaço, tab, quebra de linha e barra invertida como <c>\ooo</c> em octal.</summary>
    private static string Unescape(string field)
    {
        if (!field.Contains('\\', StringComparison.Ordinal))
        {
            return field;
        }

        var builder = new StringBuilder(field.Length);
        for (var i = 0; i < field.Length; i++)
        {
            if (field[i] == '\\' && IsOctalEscape(field, i + 1))
            {
                builder.Append((char)Convert.ToInt32(field.Substring(i + 1, OctalDigits), 8));
                i += OctalDigits;
            }
            else
            {
                builder.Append(field[i]);
            }
        }

        return builder.ToString();
    }

    private static bool IsOctalEscape(string field, int start) =>
        start + OctalDigits <= field.Length && field.AsSpan(start, OctalDigits).IndexOfAnyExcept(OctalDigitChars) < 0;
}
