namespace Duora.IntegrationTests.Media;

public sealed class NativeCorpusOptionsTests
{
    private static Dictionary<string, string?> Complete() => new()
    {
        ["DUORA_NATIVE_CORPUS_REQUIRED"] = "true",
        ["DUORA_NATIVE_CORPUS_SOCKET"] = "/run/duora-media/socket",
        ["DUORA_NATIVE_CORPUS_UID"] = "1002",
        ["DUORA_NATIVE_CORPUS_PROFILE"] = new string('a', 64),
        ["DUORA_NATIVE_CORPUS_REPORT"] = "/tmp/native-corpus.json",
    };

    [Fact]
    public void OrdinaryBaselineDoesNotRequestNativeQualification() => Assert.Null(NativeCorpusOptions.Read(_ => null));

    [Fact]
    public void CompleteExplicitConfigurationIsAccepted()
    {
        var values = Complete();
        var options = Assert.IsType<NativeCorpusOptions>(NativeCorpusOptions.Read(key => values.GetValueOrDefault(key)));
        Assert.Equal("/run/duora-media/socket", options.Socket);
        Assert.Equal(1002, options.LauncherUid);
        Assert.Equal(new string('a', 64), options.ProfileSha256);
        Assert.Equal("/tmp/native-corpus.json", options.Report);
    }

    [Theory]
    [InlineData("DUORA_NATIVE_CORPUS_REQUIRED")]
    [InlineData("DUORA_NATIVE_CORPUS_SOCKET")]
    [InlineData("DUORA_NATIVE_CORPUS_UID")]
    [InlineData("DUORA_NATIVE_CORPUS_PROFILE")]
    [InlineData("DUORA_NATIVE_CORPUS_REPORT")]
    public void PartialConfigurationCannotFallBackToTheBaseline(string missing)
    {
        var values = Complete();
        values.Remove(missing);
        Assert.Throws<InvalidOperationException>(() => NativeCorpusOptions.Read(key => values.GetValueOrDefault(key)));
    }

    [Theory]
    [InlineData("DUORA_NATIVE_CORPUS_REQUIRED", "false")]
    [InlineData("DUORA_NATIVE_CORPUS_SOCKET", "relative/socket")]
    [InlineData("DUORA_NATIVE_CORPUS_UID", "0")]
    [InlineData("DUORA_NATIVE_CORPUS_UID", "invalid")]
    [InlineData("DUORA_NATIVE_CORPUS_PROFILE", "invalid")]
    [InlineData("DUORA_NATIVE_CORPUS_REPORT", "relative.json")]
    public void InvalidConfigurationCannotQualify(string key, string value)
    {
        var values = Complete();
        values[key] = value;
        Assert.Throws<InvalidOperationException>(() => NativeCorpusOptions.Read(name => values.GetValueOrDefault(name)));
    }
}
