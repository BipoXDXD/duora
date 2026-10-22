extern alias worker;

using System.Globalization;
using worker::Duora.Worker.Media;

namespace Duora.IntegrationTests.Media;

internal sealed record NativeCorpusOptions(string Socket, int LauncherUid, string ProfileSha256, string Report)
{
    internal static NativeCorpusOptions? Read(Func<string, string?> read)
    {
        var required = read("DUORA_NATIVE_CORPUS_REQUIRED");
        var socket = read("DUORA_NATIVE_CORPUS_SOCKET");
        var uid = read("DUORA_NATIVE_CORPUS_UID");
        var pin = read("DUORA_NATIVE_CORPUS_PROFILE");
        var report = read("DUORA_NATIVE_CORPUS_REPORT");
        if (required is null && socket is null && uid is null && pin is null && report is null)
        {
            return null;
        }
        if (required != "true" || socket is null || pin is null || report is null
            || !int.TryParse(uid, NumberStyles.None, CultureInfo.InvariantCulture, out var launcherUid)
            || !Path.IsPathFullyQualified(report) || report.Contains('\0'))
        {
            throw new InvalidOperationException("Native qualification requires explicit complete configuration.");
        }
        var transport = new IsolatedPhotoSanitizerOptions
        {
            LauncherSocket = socket,
            LauncherUid = launcherUid,
            ProfileSha256 = pin,
        };
        if (!transport.IsConfigured() || !transport.IsConsistent())
        {
            throw new InvalidOperationException("Native qualification requires a valid running launcher profile.");
        }
        return new NativeCorpusOptions(socket, launcherUid, pin, report);
    }
}
