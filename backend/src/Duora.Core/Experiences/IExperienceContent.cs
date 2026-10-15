namespace Duora.Core.Experiences;

/// <summary>Conteúdo publicado de uma experiência: imutável e identificado por versão.</summary>
public interface IExperienceContent
{
    ExperienceId ExperienceId { get; }

    int ContentVersion { get; }
}
