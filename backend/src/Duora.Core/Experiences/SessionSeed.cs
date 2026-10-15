namespace Duora.Core.Experiences;

/// <summary>
/// Semente dos sorteios de uma sessão (quem recebe qual pista, quem escolhe primeiro). Gerada fora do domínio e
/// persistida com a sessão, para que o sorteio seja reproduzível.
/// </summary>
public readonly record struct SessionSeed(int Value);
