namespace Duora.IntegrationTests.Media;

internal sealed record NativeCorpusOptions(string Socket, int LauncherUid, string ProfileSha256, string Report)
{
    internal static NativeCorpusOptions? Read(Func<string, string?> read) => throw new NotImplementedException();
}
