namespace Duora.Tools.IpinfoG2;

internal sealed record G2Evidence(
    string Mode, string Sha256, long SizeBytes, string DatabaseType,
    DateTimeOffset BuiltAt, DateTimeOffset DownloadedAt, int Networks, int Ipv4Networks, int Ipv6Networks,
    bool LiteShapeMatches, bool ValidationPassed, string[] ObservedFields,
    double OpenMilliseconds, double OverlapMilliseconds, double ValidationMilliseconds,
    long AllocatedBytes, long PeakWorkingSetBytes)
{
    public static G2Evidence Measure(byte[] bytes, DateTimeOffset downloadedAt, G2Profile profile, string mode,
        CancellationToken ct) => throw new G2Failure("Unprepared");
}
