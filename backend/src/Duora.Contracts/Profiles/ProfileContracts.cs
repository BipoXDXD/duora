namespace Duora.Contracts.Profiles;

/// <summary>
/// Perfil editável do titular (API-P01). <c>Version</c> é a mesma do header <c>ETag</c>, em string decimal (spec
/// 8.1); <c>"0"</c> quando o perfil ainda não foi salvo. Nome e descrição vêm <c>null</c> enquanto não preenchidos.
/// </summary>
public sealed record OwnProfileResponse(string? DisplayName, string? Bio, string Version);

/// <summary>
/// Edição parcial do próprio perfil (API-P02), com <c>If-Match</c> obrigatório. Campo ausente fica como está;
/// <c>bio: null</c> apaga a descrição; <c>displayName: null</c> é recusado, porque o nome não se apaga, só se troca.
/// Campo desconhecido (inclusive id, dono, versão ou estado) é recusado pelo serializador. Os limites de tamanho valem
/// depois da normalização NFC e de aparar as bordas, contados em caracteres Unicode (code points).
/// </summary>
public sealed record UpdateProfileRequest
{
    public PatchField<string> DisplayName { get; init; }

    public PatchField<string> Bio { get; init; }
}
