namespace Duora.Core.Safety;

/// <summary>
/// Resposta neutra: <see cref="Unavailable"/> não diz se houve bloqueio, de quem, nem se houve sanção
/// (spec 7.3, TM-24). A fronteira responde igual a "recurso inexistente".
/// </summary>
public enum InteractionVerdict
{
    Allowed,
    Unavailable,
}
