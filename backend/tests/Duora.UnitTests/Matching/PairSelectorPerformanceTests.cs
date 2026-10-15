using System.Diagnostics;
using Duora.Core.Matching;

namespace Duora.UnitTests.Matching;

public sealed class PairSelectorPerformanceTests
{
    private const int FullEvent = 100;
    private const int Measurements = 5;
    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Pior caso de arestas da lotação inicial (spec 4.3 e 5.1): grafo completo com 100 candidatos. A primeira chamada
    /// aquece o JIT e vale a mais rápida de cinco medições, para que um runner ocupado não derrube o teste.
    /// </summary>
    [Fact]
    public void CompleteGraphOfAFullEventIsSelectedWithinBudget()
    {
        var round = CompleteGraph();
        var selection = PairSelector.Select(round);

        var fastest = Enumerable.Range(0, Measurements).Select(_ => TimeToSelect(round)).Min();

        Assert.Equal(FullEvent / 2, selection.Pairs.Count);
        Assert.True(fastest < Budget, $"Seleção levou {fastest.TotalMilliseconds} ms; orçamento de {Budget.TotalMilliseconds} ms.");
    }

    private static RoundSnapshot CompleteGraph()
    {
        var builder = new RoundBuilder();
        for (var user = 1; user <= FullEvent; user++)
        {
            builder.WithCandidate(user, waitingMinutes: user % 7);
        }

        return builder.Build();
    }

    private static TimeSpan TimeToSelect(RoundSnapshot round)
    {
        var stopwatch = Stopwatch.StartNew();
        PairSelector.Select(round);
        return stopwatch.Elapsed;
    }
}
