namespace Duora.IntegrationTests.Safety;

/// <summary>
/// T-27: uma resposta recusada não carrega nenhum ID da resposta guardada, nem no corpo nem em header algum, com ou sem
/// hífens (os ETags usam o formato <c>N</c>).
/// </summary>
internal static class ReplayLeakAssert
{
    public static void ContainsNone(HttpResponseMessage response, string body, params Guid[] ids)
    {
        var headers = string.Join('\n', response.Headers.Concat(response.Content.Headers)
            .Select(header => $"{header.Key}: {string.Join(',', header.Value)}"));
        foreach (var id in ids)
        {
            foreach (var form in new[] { id.ToString("D"), id.ToString("N") })
            {
                Assert.DoesNotContain(form, body, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(form, headers, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
