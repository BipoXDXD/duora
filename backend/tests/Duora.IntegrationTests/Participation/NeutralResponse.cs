using System.Text.RegularExpressions;

namespace Duora.IntegrationTests.Participation;

/// <summary>Descrição comparável de uma resposta que não pode revelar por que a sala não está disponível.</summary>
internal static partial class NeutralResponse
{
    // Só o traceId muda de uma requisição para outra; qualquer outra diferença seria um sinal do motivo.
    // Date e Set-Cookie variam em qualquer resposta e não dependem da sala.
    public static async Task<string> DescribeAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = TraceId().Replace(await response.Content.ReadAsStringAsync(ct), string.Empty);
        var headers = response.Headers.Concat(response.Content.Headers)
            .Where(header => header.Key is not ("Date" or "Set-Cookie" or "Content-Type"))
            .Select(header => $"{header.Key}={string.Join(',', header.Value)}")
            .Order(StringComparer.Ordinal);
        return $"{(int)response.StatusCode}|{response.Content.Headers.ContentType}|{body}|{string.Join(';', headers)}";
    }

    [GeneratedRegex("\"traceId\":\"[^\"]*\"")]
    private static partial Regex TraceId();
}
