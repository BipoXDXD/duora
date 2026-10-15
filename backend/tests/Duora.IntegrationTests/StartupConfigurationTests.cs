using Microsoft.Extensions.Options;

namespace Duora.IntegrationTests;

public sealed class StartupConfigurationTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void WebDoesNotStartWithoutConnectionString(string connectionString)
    {
        using var factory = new DuoraWebFactory(connectionString);

        var exception = Assert.Throws<OptionsValidationException>(() => factory.CreateClient());

        Assert.Contains("ConnectionStrings:Duora", exception.Message, StringComparison.Ordinal);
    }
}
