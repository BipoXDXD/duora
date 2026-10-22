using Duora.MediaProtocol;

namespace Duora.ArchitectureTests;

public sealed class SanitizerProtocolBoundaryTests
{
    [Fact]
    public void ProtocolHasNoDecoderInfrastructureOrApplicationHostDependencies()
    {
        var references = typeof(SanitizerProtocol).Assembly.GetReferencedAssemblies().Select(value => value.Name ?? "");
        Assert.Contains("Duora.Core", references);
        Assert.All(references, name => Assert.True(name == "Duora.Core" || name.StartsWith("System.", StringComparison.Ordinal), name));
        Assert.DoesNotContain(references, name => name.StartsWith("System.Net", StringComparison.Ordinal));
    }
}
