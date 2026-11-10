using System.Text.RegularExpressions;

namespace Duora.IntegrationTests;

public sealed partial class FixtureCleanupTests(PostgresFixture postgres)
{
    [Fact]
    public async Task AFailedCleanupStepNamesTheStepItsDurationAndTheStateRecordedBeforeIt()
    {
        var cause = new IOException("remoção sintética falhou");

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => FixtureCleanup.RunAsync(
            "remover o container", () => ValueTask.FromException(cause), "estado-antes-da-etapa").AsTask());

        Assert.Matches(FailureMessage(), failure.Message);
        Assert.Same(cause, failure.InnerException);
    }

    [Fact]
    public async Task ASuccessfulCleanupStepRunsTheActionOnceAndThrowsNothing()
    {
        var runs = 0;

        await FixtureCleanup.RunAsync("remover o container", () =>
        {
            runs++;
            return ValueTask.CompletedTask;
        }, "estado-antes-da-etapa");

        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task TheCleanupInventoryCountsTheServerDatabasesTheirSizeAndTheDataFiles()
    {
        var inventory = await postgres.DescribeForCleanupAsync(TestContext.Current.CancellationToken);

        // O servidor tem no mínimo postgres, duora, os dois templates selados e template0/1.
        Assert.Matches(Inventory(), inventory);
        Assert.True(int.Parse(Inventory().Match(inventory).Groups["databases"].Value, System.Globalization.CultureInfo.InvariantCulture) >= 6,
            inventory);
        Assert.DoesNotContain("indisponível", inventory, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0L, "1234\n", "1234")]
    [InlineData(1L, "0\n", "indisponível (saída 1)")]
    [InlineData(141L, "", "indisponível (saída 141)")]
    [InlineData(0L, "", "indisponível (saída 0, contagem ilegível)")]
    [InlineData(0L, "find: erro\n", "indisponível (saída 0, contagem ilegível)")]
    [InlineData(null, "12\n", "indisponível (saída desconhecida)")]
    public void AFailedFileCountIsReportedAsUnavailableInsteadOfAsZero(long? exitCode, string stdout, string expected) =>
        Assert.Equal(expected, FixtureCleanup.DescribeFileCount(exitCode, stdout));

    [GeneratedRegex("""^Limpeza "remover o container" falhou depois de \d+\.\d s \(estado-antes-da-etapa\)\.$""")]
    private static partial Regex FailureMessage();

    [GeneratedRegex("""bancos no servidor: (?<databases>\d+); tamanho somado: [1-9]\d* bytes; bancos avulsos ainda registrados: \d+; arquivos no diretório de dados: [1-9]\d*""")]
    private static partial Regex Inventory();
}
