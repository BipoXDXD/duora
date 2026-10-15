namespace Duora.Core.Experiences;

/// <summary>Ação de uma experiência; cada experiência define o seu conjunto fechado.</summary>
public interface IGameAction
{
    ActionType Type { get; }
}
