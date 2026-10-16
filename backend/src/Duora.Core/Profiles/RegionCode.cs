using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;

namespace Duora.Core.Profiles;

/// <summary>
/// Região aproximada do perfil (spec 1.1; <c>profiles.profiles.region_code</c>): uma das 27 unidades federativas em
/// ISO 3166-2:BR (<c>BR-SP</c>). Nada mais fino que a UF entra no perfil (docs/privacy/data-map.md). Só a forma
/// canônica é aceita, comparada byte a byte: minúscula, espaço na borda ou outro separador são recusados, não
/// normalizados, para o enum do contrato, o CHECK do banco e o parse concordarem sem tabela de equivalências.
/// </summary>
public sealed record RegionCode
{
    /// <summary>
    /// As 27 UFs. O conjunto só muda por emenda constitucional (o último ajuste foi em 1988, com a criação do
    /// Tocantins e de Roraima e Amapá como estados), por isso é CHECK e não tabela de referência (docs/data/model.md).
    /// </summary>
    public static IReadOnlyList<string> All { get; } =
    [
        "BR-AC", "BR-AL", "BR-AM", "BR-AP", "BR-BA", "BR-CE", "BR-DF", "BR-ES", "BR-GO", "BR-MA", "BR-MG", "BR-MS",
        "BR-MT", "BR-PA", "BR-PB", "BR-PE", "BR-PI", "BR-PR", "BR-RJ", "BR-RN", "BR-RO", "BR-RR", "BR-RS", "BR-SC",
        "BR-SE", "BR-SP", "BR-TO",
    ];

    private static readonly FrozenSet<string> Known = All.ToFrozenSet(StringComparer.Ordinal);

    private RegionCode(string value) => Value = value;

    public string Value { get; }

    public static bool TryParse(string value, [NotNullWhen(true)] out RegionCode? region)
    {
        ArgumentNullException.ThrowIfNull(value);
        region = Known.TryGetValue(value, out var canonical) ? new RegionCode(canonical) : null;
        return region is not null;
    }

    public override string ToString() => Value;
}
