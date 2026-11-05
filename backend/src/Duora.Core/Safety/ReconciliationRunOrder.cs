using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Duora.Core.Safety;

/// <summary>Uma unidade pedida no run: o par conta/medida e o digest que o moderador viu na prévia.</summary>
public sealed record ReconciliationRunUnitRef(Guid SubjectUserId, Guid OriginalMeasureId, string UnitDigest);

/// <summary>
/// Pedido de criação de um run já validado (ADR-0049, D1 e D4). Um run cabe numa página da prévia: o teto é o da
/// página, e a resposta do run não precisa de paginação. Quem cria não informa corte, itens nem versões: o servidor
/// recalcula tudo.
/// </summary>
public sealed record ReconciliationRunOrder
{
    /// <summary>Unidades por run; o mesmo teto da página da prévia.</summary>
    public const int MaxUnits = 10;

    /// <summary>Em caracteres Unicode; mesmo teto no CHECK do banco.</summary>
    public const int JustificationMaxLength = 2000;

    /// <summary>Em caracteres Unicode; mesmo teto no CHECK do banco.</summary>
    public const int AuthorizationReferenceMaxLength = 200;

    private ReconciliationRunOrder(IReadOnlyList<ReconciliationRunUnitRef> units, string justification, string authorizationReference)
    {
        Units = units;
        Justification = justification;
        AuthorizationReference = authorizationReference;
    }

    public IReadOnlyList<ReconciliationRunUnitRef> Units { get; }

    public string Justification { get; }

    public string AuthorizationReference { get; }

    /// <summary>Pedido válido, ou recusa em que o <c>ParamName</c> nomeia o campo do corpo.</summary>
    /// <exception cref="ArgumentException">Campo ausente, fora do limite ou com caractere de controle.</exception>
    public static ReconciliationRunOrder Create(
        IReadOnlyList<ReconciliationRunUnitRef>? units,
        string? justification,
        string? authorizationReference)
    {
        RequireUnits(units);
        return new ReconciliationRunOrder(
            units,
            RequireLine(justification, JustificationMaxLength, nameof(justification)),
            RequireLine(authorizationReference, AuthorizationReferenceMaxLength, nameof(authorizationReference)));
    }

    /// <summary>
    /// SHA-256 do pedido, para a mesma chave com outro pedido virar 409. Cada texto leva o tamanho na frente, para a
    /// fronteira entre eles contar; as unidades entram em ordem canônica, porque a ordem no corpo não muda o pedido.
    /// </summary>
    public byte[] Intent()
    {
        var text = new StringBuilder("v1\n");
        Field(text, Justification);
        Field(text, AuthorizationReference);
        foreach (var unit in Units.Select(Canonical).Order(StringComparer.Ordinal))
        {
            text.Append(unit).Append('\n');
        }

        return SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()));
    }

    private static void RequireUnits([System.Diagnostics.CodeAnalysis.NotNull] IReadOnlyList<ReconciliationRunUnitRef>? units)
    {
        ArgumentNullException.ThrowIfNull(units);
        if (units.Count is 0 or > MaxUnits)
        {
            throw new ArgumentOutOfRangeException(nameof(units), $"De 1 a {MaxUnits} unidades.");
        }

        if (units.Any(unit => unit.SubjectUserId == Guid.Empty || unit.OriginalMeasureId == Guid.Empty
            || !ReconciliationUnitDigest.IsWellFormed(unit.UnitDigest)))
        {
            throw new ArgumentException("Unidade malformada.", nameof(units));
        }

        if (units.DistinctBy(unit => (unit.SubjectUserId, unit.OriginalMeasureId)).Count() != units.Count)
        {
            throw new ArgumentException("Unidade repetida.", nameof(units));
        }
    }

    /// <summary>Como o texto livre do Safety, mas numa linha só: nem quebra de linha nem tabulação (ADR-0049, D4).</summary>
    private static string RequireLine(string? value, int maxLength, string paramName)
    {
        ArgumentNullException.ThrowIfNull(value, paramName);
        if (value.Any(char.IsControl))
        {
            throw new ArgumentException("Contém caractere de controle.", paramName);
        }

        return SafetyText.RequireText(value, maxLength, paramName);
    }

    private static void Field(StringBuilder text, string value) =>
        text.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append('\n');

    private static string Canonical(ReconciliationRunUnitRef unit) => string.Create(
        CultureInfo.InvariantCulture, $"{unit.SubjectUserId:D};{unit.OriginalMeasureId:D};{unit.UnitDigest}");
}
