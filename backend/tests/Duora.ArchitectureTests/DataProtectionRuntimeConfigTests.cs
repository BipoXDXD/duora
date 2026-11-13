using System.Text.Json;

namespace Duora.ArchitectureTests;

/// <summary>
/// O teto de novas tentativas do DefaultKeyResolver vem do Directory.Build.props, não do código. Os testes de
/// integração leem o runtimeconfig do próprio executável de testes; este confere o do Duora.Web, que é o que a
/// produção carrega, para que a opção não suma da produção sem ninguém notar.
/// </summary>
public sealed class DataProtectionRuntimeConfigTests
{
    private const string KeyResolverRetries =
        "Microsoft.AspNetCore.DataProtection.KeyManagement.MaximumTotalDefaultKeyResolverRetries";

    [Fact]
    public void WebRuntimeConfigDisablesKeyResolverRetries()
    {
        var runtimeConfig = Path.Combine(AppContext.BaseDirectory, "Duora.Web.runtimeconfig.json");
        using var json = JsonDocument.Parse(File.ReadAllText(runtimeConfig));
        var properties = json.RootElement.GetProperty("runtimeOptions").GetProperty("configProperties");

        Assert.True(properties.TryGetProperty(KeyResolverRetries, out var retries), $"{KeyResolverRetries} ausente.");
        Assert.Equal(0, retries.GetInt32());
    }
}
