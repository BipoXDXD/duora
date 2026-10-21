using Duora.Web.Http;

namespace Duora.Web.Media;

/// <summary>Request-local presentation of the two upload header validation refusals (ADR-0034).</summary>
internal static class UploadValidationFormat
{
    internal const string Header = "Duora-Validation-Format";
    internal const string Codes = "codes-v1";

    internal static IResult Refuse(HttpRequest request, string field, string legacy, string code)
    {
        var selector = request.Headers[Header];
        var codes = selector.Count == 1 && string.Equals(selector[0]?.Trim(' ', '\t'), Codes, StringComparison.Ordinal);
        var response = request.HttpContext.Response;
        var vary = response.Headers.Vary.SelectMany(value => (value ?? "").Split(',')).Select(value => value.Trim());
        if (!vary.Any(value => value == "*" || string.Equals(value, Header, StringComparison.OrdinalIgnoreCase)))
        {
            response.Headers.Append("Vary", Header);
        }
        if (codes)
        {
            response.Headers[Header] = Codes;
        }
        return ApiProblems.Validation(new Dictionary<string, string[]> { [field] = [codes ? code : legacy] });
    }
}
