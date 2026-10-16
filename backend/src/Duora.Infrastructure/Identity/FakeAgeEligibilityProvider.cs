using Duora.Core.Identity;

namespace Duora.Infrastructure.Identity;

/// <summary>
/// Fornecedor simulado para desenvolvimento e testes (spec 12.2: pessoas sintéticas, respostas simuladas). Os modos
/// roteirizados reproduzem o fornecedor lento, fora do ar e o callback repetido. A abertura é idempotente: a
/// referência deriva só do id da solicitação, então repetir a chamada devolve a mesma sessão.
/// </summary>
internal sealed class FakeAgeEligibilityProvider(FakeAgeProviderOptions options, TimeProvider time) : IAgeEligibilityProvider
{
    public const string ProviderName = "fake";

    public string Name => ProviderName;

    public FakeAgeProviderMode Mode => options.Mode;

    public static string ReferenceFor(Guid verificationId) => $"fake_{verificationId:N}";

    public async Task<AgeCheckSession> StartAsync(AgeCheckStart request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        switch (options.Mode)
        {
            case FakeAgeProviderMode.Fail:
                throw new HttpRequestException("Fornecedor simulado fora do ar.");
            case FakeAgeProviderMode.Slow:
                await Task.Delay(options.SlowDelay, time, cancellationToken);
                break;
            case FakeAgeProviderMode.Succeed:
            case FakeAgeProviderMode.DuplicateCallback:
                break;
            default:
                throw new InvalidOperationException($"Modo do fornecedor simulado sem tratamento: {options.Mode}.");
        }

        var reference = ReferenceFor(request.VerificationId);
        return new AgeCheckSession(reference, new Uri($"{options.FlowBaseUrl.TrimEnd('/')}/{reference}"));
    }
}

/// <summary>Sem fornecedor configurado. Nunca é chamado: quem usa confere <see cref="AgeVerificationOptions.IsEnabled"/>.</summary>
internal sealed class NoAgeEligibilityProvider : IAgeEligibilityProvider
{
    public string Name => "none";

    public Task<AgeCheckSession> StartAsync(AgeCheckStart request, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Nenhum fornecedor de verificação etária configurado.");
}
