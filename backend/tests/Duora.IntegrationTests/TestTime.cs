namespace Duora.IntegrationTests;

/// <summary>Passos de relógio que os testes de prazo usam para ficar logo antes de um instante.</summary>
internal static class TestTime
{
    /// <summary>
    /// A menor resolução de <see cref="DateTimeOffset"/>: um instante a <c>OneTick</c> antes do prazo ainda está antes
    /// dele. Só vale para <c>- OneTick</c>: o serviço trunca o relógio para microssegundo, e um <c>+ OneTick</c> cairia
    /// exatamente no prazo.
    /// </summary>
    public static readonly TimeSpan OneTick = TimeSpan.FromTicks(1);
}
