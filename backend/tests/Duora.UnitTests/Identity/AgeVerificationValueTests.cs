using Duora.Core.Identity;

namespace Duora.UnitTests.Identity;

public sealed class AgeVerificationValueTests
{
    private static readonly Uri FlowUrl = new("https://verificacao.example/fluxo/abc");

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("versão")]
    [InlineData("1234567890123456789012345678901234")]
    public void PolicyVersionUsesTheDocumentVersionFormat(string version)
    {
        Assert.ThrowsAny<ArgumentException>(
            () => new AgeVerificationPolicy(version, TimeSpan.FromHours(1)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void PolicyNeedsAPositiveRequestLifetime(int lifetimeMinutes)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new AgeVerificationPolicy("2026-10-15", TimeSpan.FromMinutes(lifetimeMinutes)));
    }

    [Fact]
    public void PolicyKeepsItsValues()
    {
        var policy = new AgeVerificationPolicy("v1.2_a-b", TimeSpan.FromHours(2));

        Assert.Equal("v1.2_a-b", policy.Version);
        Assert.Equal(TimeSpan.FromHours(2), policy.RequestLifetime);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ref com espaço")]
    [InlineData("ref\u0000")]
    [InlineData("referência")]
    public void ProviderReferenceIsPrintableAsciiWithoutSpaces(string reference)
    {
        Assert.ThrowsAny<ArgumentException>(() => new AgeCheckSession(reference, FlowUrl));
    }

    [Fact]
    public void ProviderReferenceHasACeilingOf200Characters()
    {
        Assert.NotNull(new AgeCheckSession(new string('a', AgeCheckSession.MaxReferenceLength), FlowUrl));
        Assert.Throws<ArgumentException>(() => new AgeCheckSession(new string('a', AgeCheckSession.MaxReferenceLength + 1), FlowUrl));
    }

    [Theory]
    [InlineData("http://verificacao.example/fluxo")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://user:senha@verificacao.example/fluxo")]
    public void FlowUrlMustBeHttpsWithoutCredentials(string url)
    {
        Assert.Throws<ArgumentException>(() => new AgeCheckSession("ref-1", new Uri(url)));
    }

    [Fact]
    public void FlowUrlMustBeAbsolute()
    {
        Assert.Throws<ArgumentException>(() => new AgeCheckSession("ref-1", new Uri("/fluxo", UriKind.Relative)));
    }

    [Fact]
    public void FlowUrlHasACeilingOf2048Characters()
    {
        var longUrl = new Uri("https://verificacao.example/" + new string('a', AgeCheckSession.MaxFlowUrlLength));

        Assert.Throws<ArgumentException>(() => new AgeCheckSession("ref-1", longUrl));
    }

    [Fact]
    public void StartRequestCarriesTheIdsTheAdapterMayNeed()
    {
        var verificationId = Guid.CreateVersion7();
        var start = new AgeCheckStart(verificationId, "nonce-1", new Uri("https://duora.example/verificacao"), "2026-10-15");

        Assert.Equal(verificationId, start.VerificationId);
        Assert.Equal(verificationId.ToString("N"), start.IdempotencyKey);
    }

    [Fact]
    public void StartRequestRejectsAnEmptyIdOrNonce()
    {
        var returnUrl = new Uri("https://duora.example/verificacao");

        Assert.Throws<ArgumentException>(() => new AgeCheckStart(Guid.Empty, "nonce-1", returnUrl, "v1"));
        Assert.ThrowsAny<ArgumentException>(() => new AgeCheckStart(Guid.CreateVersion7(), "", returnUrl, "v1"));
    }
}
