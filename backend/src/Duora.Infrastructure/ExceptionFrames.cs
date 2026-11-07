using System.Diagnostics;
using System.Globalization;

namespace Duora.Infrastructure;

/// <summary>
/// Localiza um defeito no log sem expor conteúdo (spec 19.2): só tipo, método e linha. A mensagem da exceção e o
/// caminho do arquivo ficam de fora, porque mensagem de fornecedor ou banco pode carregar dado.
/// </summary>
public static class ExceptionFrames
{
    /// <summary>O loop repete o erro a cada volta: o limite mantém cada linha de log pequena.</summary>
    private const int MaximumFrames = 8;

    private const string AwaitPlumbingNamespace = "System.Runtime";

    public static string Describe(Exception exception) =>
        string.Join(" < ", new StackTrace(exception, fNeedFileInfo: true).GetFrames()
            .Where(IsApplicationCode)
            .Take(MaximumFrames)
            .Select(DescribeFrame));

    private static bool IsApplicationCode(StackFrame frame) =>
        frame.GetMethod()?.DeclaringType?.Namespace?.StartsWith(AwaitPlumbingNamespace, StringComparison.Ordinal) == false;

    private static string DescribeFrame(StackFrame frame)
    {
        var method = frame.GetMethod();
        var line = frame.GetFileLineNumber();
        var location = $"{method?.DeclaringType?.FullName}.{method?.Name}";
        return line > 0 ? string.Create(CultureInfo.InvariantCulture, $"{location}:line {line}") : location;
    }
}
