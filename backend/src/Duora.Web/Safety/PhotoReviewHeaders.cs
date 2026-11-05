using System.Globalization;
using Duora.Core.Media;
using Duora.Web.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Duora.Web.Safety;

internal static class PhotoReviewHeaders
{
    internal static string Tag(string kind, Guid id, long version) =>
        $"\"{kind}-{id:N}-{version.ToString(CultureInfo.InvariantCulture)}\"";

    internal static ProblemHttpResult? Command(HttpContext context, string kind, Guid id, out long version, out IdempotencyKey key)
    {
        key = default;
        if (IfMatchVersion(context, kind, id, out version) is { } refused)
        {
            return refused;
        }
        return IdempotencyKey.TryParse(context.Request.Headers["Idempotency-Key"].ToString(), out key)
            ? null : ApiProblems.Problem(400, "IDEMPOTENCY_KEY_INVALID");
    }

    /// <summary>A versão do <c>If-Match</c> desta entidade: ausente ou <c>*</c> é 428; outra forma ou outra entidade, 412.</summary>
    internal static ProblemHttpResult? IfMatchVersion(HttpContext context, string kind, Guid id, out long version)
    {
        version = 0;
        var match = context.Request.Headers.IfMatch.ToString();
        if (string.IsNullOrWhiteSpace(match) || match.Trim() == "*")
        {
            return ApiProblems.Problem(428, "PRECONDITION_REQUIRED");
        }
        var prefix = $"\"{kind}-{id:N}-";
        if (!match.StartsWith(prefix, StringComparison.Ordinal) || !match.EndsWith('"')
            || !long.TryParse(match.AsSpan(prefix.Length, match.Length - prefix.Length - 1),
                NumberStyles.None, CultureInfo.InvariantCulture, out version)
            || version <= 0 || Tag(kind, id, version) != match)
        {
            return ApiProblems.Problem(412, "PRECONDITION_FAILED");
        }
        return null;
    }
}
