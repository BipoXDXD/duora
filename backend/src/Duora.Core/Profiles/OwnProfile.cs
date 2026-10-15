namespace Duora.Core.Profiles;

/// <summary>
/// Perfil como o titular o edita (API-P01). <c>Version</c> é o token de concorrência otimista (spec 3.4): começa em 0
/// enquanto nada foi salvo e sobe a cada gravação.
/// </summary>
public sealed record OwnProfile(DisplayName? DisplayName, ProfileBio? Bio, long Version)
{
    /// <summary>Perfil de quem ainda não salvou nada: ler não cria linha no banco.</summary>
    public static OwnProfile Unsaved { get; } = new(null, null, 0);
}

/// <summary>
/// Alteração parcial já validada (API-P02). Campo <c>null</c> aqui significa "não mexer"; apagar a descrição é um
/// <see cref="BioChange"/> com valor <c>null</c>.
/// </summary>
public sealed record ProfileEdit(DisplayName? DisplayName, BioChange? Bio)
{
    public bool ChangesNothing => DisplayName is null && Bio is null;
}

/// <summary>Troca da descrição; <c>NewBio</c> <c>null</c> apaga.</summary>
public sealed record BioChange(ProfileBio? NewBio);
