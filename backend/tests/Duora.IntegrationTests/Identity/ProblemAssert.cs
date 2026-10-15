using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Duora.IntegrationTests.Identity;

internal static class ProblemAssert
{
    /// <summary>
    /// Confere o contrato de erro (spec 8.1): status, <c>application/problem+json</c>, <c>type</c>, <c>title</c>,
    /// <c>code</c> e <c>traceId</c>, sem stack trace nem nome de exceção. Devolve o corpo para asserts extras.
    /// </summary>
    public static async Task<JsonObject> IsProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var body = JsonNode.Parse(text)?.AsObject() ?? throw new InvalidOperationException("Corpo vazio.");
        Assert.Equal(code, (string?)body["code"]);
        Assert.Equal((int)status, (int?)body["status"]);
        Assert.False(string.IsNullOrEmpty((string?)body["type"]));
        Assert.False(string.IsNullOrEmpty((string?)body["title"]));
        Assert.False(string.IsNullOrEmpty((string?)body["traceId"]));
        Assert.DoesNotContain("Exception", text, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", text, StringComparison.Ordinal);
        return body;
    }

    /// <summary>Segundos do header Retry-After de um 429; falha se o header faltar ou não for positivo.</summary>
    public static int RetryAfterSeconds(HttpResponseMessage response)
    {
        var seconds = int.Parse(
            Assert.Single(response.Headers.GetValues("Retry-After")),
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(seconds > 0, $"Retry-After deveria ser positivo, veio {seconds}.");
        return seconds;
    }

    /// <summary>Corpo sem o <c>traceId</c>, que muda por requisição, para comparar respostas que devem ser iguais.</summary>
    public static async Task<string> BodyWithoutTraceIdAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        if (text.Length == 0)
        {
            return "";
        }

        var body = JsonNode.Parse(text)?.AsObject() ?? throw new InvalidOperationException("Corpo não é objeto.");
        body.Remove("traceId");
        return body.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }
}
