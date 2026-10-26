namespace Duora.MediaCorpusTests;

public sealed class HarnessBoundaryTests
{
    [Fact]
    public void CorpusAssemblyHasNoDatabaseFixtureOrTestcontainersReference()
    {
        var assembly = typeof(HarnessBoundaryTests).Assembly;
        Assert.DoesNotContain(assembly.GetCustomAttributesData(), attribute =>
            attribute.AttributeType.Name == "AssemblyFixtureAttribute");
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference =>
            reference.Name is not null &&
            (reference.Name.Contains("Testcontainers", StringComparison.OrdinalIgnoreCase) ||
             reference.Name == "Duora.IntegrationTests"));
    }
}
