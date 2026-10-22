using System.Text.Json;
using System.Text.Json.Serialization;

namespace Duora.Tools.IpinfoG2;

internal sealed record G2Profile(
    string Authorization, long MaxBytes, int MaxNetworks, int TimeoutSeconds,
    string[] RedirectHosts, string? DatabaseType = null, string? ExpectedSha256 = null)
{
    private const string ApprovalPrefix = "https://github.com/BipoXDXD/duora/issues/108#issuecomment-";
    private static readonly JsonSerializerOptions JsonOptions = new()
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private static readonly HashSet<string> DocumentedHosts = new(StringComparer.Ordinal)
    { "ipinfo.io", "dl.assets.ipinfo.io", "dl.ipinfo.io", "storage.googleapis.com" };

    public static G2Profile Parse(string json, string mode)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new() { MaxDepth = 8 });
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || document.RootElement.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count()
                    != document.RootElement.EnumerateObject().Count())
            { throw new G2Failure("Configuration"); }
            var profile = document.Deserialize<G2Profile>(JsonOptions) ?? throw new G2Failure("Configuration");
            profile.Validate(mode);
            return profile;
        }
        catch (JsonException) { throw new G2Failure("Configuration"); }
    }

    public void Validate(string mode)
    {
        if (mode is not ("inventory" or "validate") || Authorization is null || Authorization.Length > 256
            || !Authorization.StartsWith(ApprovalPrefix, StringComparison.Ordinal)
            || Authorization.Length == ApprovalPrefix.Length || !Authorization[ApprovalPrefix.Length..].All(char.IsAsciiDigit)
            || MaxBytes is <= 0 or > int.MaxValue || MaxNetworks is <= 0 or > 50000000
            || TimeoutSeconds is <= 0 or > 600 || RedirectHosts is null
            || RedirectHosts.Any(host => !DocumentedHosts.Contains(host))
            || RedirectHosts.Distinct(StringComparer.Ordinal).Count() != RedirectHosts.Length
            || (mode == "inventory" && (DatabaseType is not null || ExpectedSha256 is not null))
            || (mode == "validate" && (!IsSafeType(DatabaseType) || !IsHash(ExpectedSha256))))
        { throw new G2Failure("Configuration"); }
    }

    public static bool IsSafeType(string? value) => value is { Length: > 0 and <= 128 }
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ' ');

    private static bool IsHash(string? value) => value is { Length: 64 }
        && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}

internal sealed class G2Failure(string code) : Exception("G2 rehearsal failed.")
{
    public string Code { get; } = code;
}
