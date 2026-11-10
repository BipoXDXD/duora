using System.Diagnostics;
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

    [Fact]
    public async Task AnInventoryThatThrowsStillLetsTheRemovalRunAndTheFailureSaysWhy()
    {
        var removals = 0;

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => FixtureCleanup.RemoveAfterInventoryAsync(
            "remover o container",
            _ => throw new InvalidOperationException("inventário sintético quebrou"),
            ShortInventoryCeiling,
            () =>
            {
                removals++;
                return ValueTask.FromException(new IOException("remoção sintética falhou"));
            }, NoRecord).AsTask());

        Assert.Equal(1, removals);
        Assert.Contains("inventário indisponível: InvalidOperationException: inventário sintético quebrou", failure.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnInventoryThatHangsIgnoringTheTokenIsCutAtTheCeilingAndTheRemovalStillRuns()
    {
        var removals = 0;
        var started = Stopwatch.GetTimestamp();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => FixtureCleanup.RemoveAfterInventoryAsync(
            "remover o container",
            _ => new TaskCompletionSource<string>().Task,
            ShortInventoryCeiling,
            () =>
            {
                removals++;
                return ValueTask.FromException(new IOException("remoção sintética falhou"));
            }, NoRecord).AsTask());

        Assert.Equal(1, removals);
        Assert.Contains("inventário indisponível: TimeoutException", failure.Message, StringComparison.Ordinal);
        Assert.True(Stopwatch.GetElapsedTime(started) < InventoryHangBound, $"{Stopwatch.GetElapsedTime(started)}");
    }

    [Fact]
    public async Task ASuccessfulRemovalRecordsItsDurationAndTheInventoryOnce()
    {
        var records = new List<string>();

        await FixtureCleanup.RemoveAfterInventoryAsync("remover o container", _ => Task.FromResult("estado-antes-da-etapa"),
            ShortInventoryCeiling, () => ValueTask.CompletedTask, records.Add);

        var record = Assert.Single(records);
        Assert.Matches(SuccessRecord(), record);
    }

    [Fact]
    public async Task AFailedRemovalRecordsNothingBecauseItsExceptionCarriesTheDuration()
    {
        var records = new List<string>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => FixtureCleanup.RemoveAfterInventoryAsync(
            "remover o container", _ => Task.FromResult("estado-antes-da-etapa"), ShortInventoryCeiling,
            () => ValueTask.FromException(new IOException("remoção sintética falhou")), records.Add).AsTask());

        Assert.Empty(records);
    }

    private static void NoRecord(string record) => _ = record;

    /// <summary>Teto curto do inventário nos testes; o do fixture é de 30 s.</summary>
    private static readonly TimeSpan ShortInventoryCeiling = TimeSpan.FromMilliseconds(200);

    /// <summary>Folga ampla sobre o teto curto: o inventário travado nunca segura a remoção por perto disso.</summary>
    private static readonly TimeSpan InventoryHangBound = TimeSpan.FromSeconds(10);

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

    [GeneratedRegex("""^Limpeza "remover o container" levou \d+\.\d s \(estado-antes-da-etapa\)\.$""")]
    private static partial Regex SuccessRecord();

    [GeneratedRegex("""bancos no servidor: (?<databases>\d+); tamanho somado: [1-9]\d* bytes; bancos avulsos ainda registrados: \d+; arquivos no diretório de dados: [1-9]\d*""")]
    private static partial Regex Inventory();
}
