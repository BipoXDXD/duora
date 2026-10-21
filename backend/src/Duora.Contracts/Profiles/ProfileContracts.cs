namespace Duora.Contracts.Profiles;

/// <summary>
/// Perfil editável do titular (API-P01). <c>Version</c> é a mesma do header <c>ETag</c>, em string decimal (spec
/// 8.1); <c>"0"</c> quando o perfil ainda não foi salvo. Nome, descrição e região vêm <c>null</c> enquanto não
/// preenchidos. CountryCode e SubdivisionCode são opcionais. RegionCode preserva a projeção legada de UF,
/// ou null para localização não informada ou somente país.
/// </summary>
public sealed record OwnProfileResponse(string? DisplayName, string? Bio, string? RegionCode, string Version, string? CountryCode = null, string? SubdivisionCode = null);

/// <summary>
/// Edição parcial do próprio perfil (API-P02), com <c>If-Match</c> obrigatório. Campo ausente fica como está;
/// <c>bio: null</c> apaga a descrição; <c>regionCode: null</c> volta a região para "não informada";
/// <c>displayName: null</c> é recusado, porque o nome não se apaga, só se troca. <c>regionCode</c> aceita só uma das 27
/// UFs em ISO 3166-2:BR, na forma canônica (<c>BR-SP</c>; <c>br-sp</c> é recusado).
/// Campo desconhecido (inclusive id, dono, versão ou estado) é recusado pelo serializador. Os limites de tamanho valem
/// depois da normalização NFC e de aparar as bordas, contados em caracteres Unicode (code points).
/// </summary>
public sealed record UpdateProfileRequest
{
    public PatchField<string> DisplayName { get; init; }

    public PatchField<string> Bio { get; init; }

    public PatchField<string> RegionCode { get; init; }

    /// <summary>Location replacement: null clears location; omitted subdivision means country only.
    /// Do not combine with regionCode. Omit both new fields to retain location.</summary>
    public PatchField<string> CountryCode { get; init; }

    /// <summary>Optional first-level ISO 3166-2 subdivision. Requires countryCode in the same request.</summary>
    public PatchField<string> SubdivisionCode { get; init; }
}
