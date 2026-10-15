namespace Duora.Core.Experiences;

/// <summary>
/// Semente dos sorteios feitos ao criar a sessão (quem recebe qual pista, quem escolhe primeiro), gerada fora do
/// domínio. <see cref="System.Random"/> com semente não garante a mesma sequência entre versões do runtime, por isso
/// a reprodutibilidade vem do <b>resultado</b> do sorteio, gravado no estado da sessão, e não de repetir o sorteio.
/// </summary>
public readonly record struct SessionSeed(int Value);
