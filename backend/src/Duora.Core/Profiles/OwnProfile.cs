namespace Duora.Core.Profiles;

/// <summary>
/// Perfil como o titular o lê (API-P01). <c>Version</c> é o token de concorrência otimista (spec 3.4): começa em 0
/// enquanto nada foi salvo e sobe a cada gravação. Os textos são o que está gravado, sem passar de novo pela regra de
/// entrada (<see cref="Profiles.DisplayName"/> e <see cref="ProfileBio"/> valem só para a escrita): se a regra
/// endurecer, uma linha antiga continua legível, e o CHECK do banco é o mínimo garantido. <c>RegionCode</c> é a UF em
/// ISO 3166-2:BR, ou <c>null</c> quando a pessoa não informou.
/// </summary>
public sealed record OwnProfile(string? DisplayName, string? Bio, string? RegionCode, long Version)
{
    /// <summary>Perfil de quem ainda não salvou nada: ler não cria linha no banco.</summary>
    public static OwnProfile Unsaved { get; } = new(null, null, null, 0);
}

/// <summary>
/// Alteração parcial já validada (API-P02). Campo <c>null</c> aqui significa "não mexer"; apagar a descrição é um
/// <see cref="BioChange"/> com valor <c>null</c>, e deixar de informar a região é um <see cref="RegionChange"/> com
/// valor <c>null</c>.
/// </summary>
public sealed record ProfileEdit(DisplayName? DisplayName, BioChange? Bio, RegionChange? Region)
{
    public bool ChangesNothing => DisplayName is null && Bio is null && Region is null;
}

/// <summary>Troca da descrição; <c>NewBio</c> <c>null</c> apaga.</summary>
public sealed record BioChange(ProfileBio? NewBio);

/// <summary>Troca da região; <c>NewRegion</c> <c>null</c> volta a "não informada".</summary>
public sealed record RegionChange(RegionCode? NewRegion);
