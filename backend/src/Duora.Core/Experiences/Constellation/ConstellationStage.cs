namespace Duora.Core.Experiences.Constellation;

/// <summary>Etapas de Constelação de Escolhas (spec 6.3), na ordem em que acontecem.</summary>
public enum ConstellationStage
{
    /// <summary>Um cartão por vez: cada pessoa escolhe em privado ou pula; a revelação vem com as duas respostas.</summary>
    Cards = 1,

    /// <summary>Encerramento: só o que foi revelado, sem pontuação.</summary>
    Summary = 2,
}
