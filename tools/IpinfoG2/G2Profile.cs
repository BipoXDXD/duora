namespace Duora.Tools.IpinfoG2;

internal sealed record G2Profile(
    string Authorization, long MaxBytes, int MaxNetworks, int TimeoutSeconds,
    string[] RedirectHosts, string? DatabaseType = null, string? ExpectedSha256 = null)
{
    public void Validate(string mode) { }
}

internal sealed class G2Failure(string code) : Exception("G2 rehearsal failed.")
{
    public string Code { get; } = code;
}
