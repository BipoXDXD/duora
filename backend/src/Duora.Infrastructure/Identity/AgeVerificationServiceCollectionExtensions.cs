using Duora.Core;
using Duora.Core.Identity;
using Duora.Infrastructure.Email;
using Duora.Infrastructure.Outbox;
using Duora.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Duora.Infrastructure.Identity;

public static class AgeVerificationServiceCollectionExtensions
{
    /// <summary>
    /// Verificação etária comum a web e worker (ADR-0024): options validadas no start, fornecedor configurado e os
    /// casos de uso. O host não sobe com o fornecedor <c>Fake</c> fora de Development e Testing, nem com política ou
    /// allowlist inválidas. Sem <c>AgeVerification:Provider</c>, o fluxo fica desligado.
    /// </summary>
    public static IServiceCollection AddAgeVerification(this IServiceCollection services)
    {
        services.AddOptions<AgeVerificationOptions>()
            .BindConfiguration(AgeVerificationOptions.Section)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<AgeVerificationOptions>, AgeVerificationOptionsValidator>();

        services.AddSingleton<IAgeEligibilityProvider>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<AgeVerificationOptions>>().Value;
            return options.Provider switch
            {
                AgeVerificationProviderKind.Fake => new FakeAgeEligibilityProvider(
                    options.Fake,
                    provider.GetRequiredService<TimeProvider>()),
                AgeVerificationProviderKind.None => new NoAgeEligibilityProvider(),
                _ => throw new InvalidOperationException($"Fornecedor etário sem adaptador: {options.Provider}."),
            };
        });
        services.AddScoped(provider => new AgeVerificationService(
            provider.GetRequiredService<DuoraDbContext>(),
            provider.GetRequiredService<IOptions<AgeVerificationOptions>>().Value,
            provider.GetRequiredService<IAgeEligibilityProvider>().Name,
            provider.GetRequiredService<TimeProvider>()));

        return services;
    }

    /// <summary>
    /// Consumidor da outbox que abre a verificação no fornecedor (só o worker). Parâmetros em
    /// <c>AgeVerification:Dispatch</c>, com os mesmos nomes e regras da seção <c>Outbox</c> do e-mail; o timeout
    /// padrão da chamada é menor que o do envio de e-mail.
    /// </summary>
    public static IServiceCollection AddAgeVerificationDispatch(this IServiceCollection services)
    {
        services.AddOptions<OutboxDispatchOptions>(AgeVerificationDispatcher.DispatchOptionsName)
            .Configure(o => o.SendTimeout = TimeSpan.FromSeconds(10))
            .BindConfiguration($"{AgeVerificationOptions.Section}:Dispatch")
            .Validate(o => o.IsConsistent(), "AgeVerification:Dispatch incoerente: o lease deve durar mais que a chamada.")
            .ValidateOnStart();

        services.TryAddSingleton(provider => new OutboxDeliveryQueue(provider.GetRequiredService<NpgsqlDataSource>()));
        services.AddSingleton(provider => new AgeVerificationDispatcher(
            provider.GetRequiredService<OutboxDeliveryQueue>(),
            provider.GetRequiredService<NpgsqlDataSource>(),
            provider.GetRequiredService<IAgeEligibilityProvider>(),
            provider.GetRequiredService<IOptions<AgeVerificationOptions>>().Value,
            provider.GetRequiredService<IOptionsMonitor<OutboxDispatchOptions>>().Get(AgeVerificationDispatcher.DispatchOptionsName),
            provider.GetRequiredService<PublicOrigin>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<AgeVerificationDispatcher>>()));

        return services;
    }

    private static bool IsDevelopmentOrTesting(IHostEnvironment environment) =>
        environment.IsDevelopment() || environment.IsEnvironment("Testing");

    /// <summary>Põe na mensagem de falha do start a chave exata a corrigir.</summary>
    private sealed class AgeVerificationOptionsValidator(IHostEnvironment environment) : IValidateOptions<AgeVerificationOptions>
    {
        public ValidateOptionsResult Validate(string? name, AgeVerificationOptions options) =>
            options.FindProblem(IsDevelopmentOrTesting(environment)) is { } problem
                ? ValidateOptionsResult.Fail(problem)
                : ValidateOptionsResult.Success;
    }
}
