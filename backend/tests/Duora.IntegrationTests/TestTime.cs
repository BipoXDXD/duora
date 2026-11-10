namespace Duora.IntegrationTests;

/// <summary>Passos de relógio que os testes de prazo usam para ficar logo antes ou logo depois de um instante.</summary>
internal static class TestTime
{
    /// <summary>A menor resolução de <see cref="DateTimeOffset"/>: um instante a <c>OneTick</c> do prazo ainda está do outro lado dele.</summary>
    public static readonly TimeSpan OneTick = TimeSpan.FromTicks(1);
}
