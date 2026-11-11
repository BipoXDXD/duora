namespace Duora.Web.Http;

/// <summary>
/// Marca as rotas públicas de identidade (o grupo <c>/auth</c> inteiro): todo 503 delas responde com o mesmo
/// <c>Retry-After</c> da conta ocupada, qualquer que seja a causa, para o header não dizer se a conta existe nem o que
/// falhou no servidor (decisão da coordenação, 10/11; issues #465 e #468).
/// </summary>
internal sealed class NeutralUnavailabilityEndpoint;
