using Duora.Core.Notifications;
using Duora.Infrastructure.Outbox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Duora.Infrastructure.Email;

public static class EmailServiceCollectionExtensions
{
    /// <summary>
    /// Registra o consumidor da outbox de e-mail e o adaptador de envio. O host não sobe sem
    /// configuração válida para o provedor selecionado nem com parâmetros de <c>Outbox</c> incoerentes.
    /// </summary>
    public static IServiceCollection AddTransactionalEmailDispatch(this IServiceCollection services)
    {
        services.AddOptions<EmailTransportOptions>()
            .BindConfiguration("Email")
            .Validate(o => o.IsValid(), "Email:Provider deve ser PickupDirectory ou Resend; Resend exige Email:Resend:ApiKey e Email:Resend:From válidos.")
            .ValidateOnStart();
        services.AddOptions<PickupDirectoryOptions>()
            .BindConfiguration(PickupDirectoryOptions.Section)
            .Validate<IOptions<EmailTransportOptions>>(
                (o, transport) => transport.Value.Provider != "PickupDirectory" || !string.IsNullOrWhiteSpace(o.PickupDirectory),
                "Email:PickupDirectory é obrigatório (docs/development/environment.md).")
            .ValidateOnStart();

        services.AddOptions<OutboxDispatchOptions>()
            .BindConfiguration(OutboxDispatchOptions.Section)
            .Validate(o => o.IsConsistent(), "Parâmetros de Outbox incoerentes: o lease deve durar mais que o envio.")
            .Validate(
                o => o.HasSendLeaseBudget(),
                "Outbox: SendTimeout deve ser menor ou igual a LeaseDuration / 2 (o lease é renovado antes de cada envio).")
            .ValidateOnStart();

        services.AddKeyedSingleton<HttpClient>("Resend", (_, _) => ResendEmailSender.CreateClient());
        services.AddSingleton<ITransactionalEmailSender>(provider =>
            provider.GetRequiredService<IOptions<EmailTransportOptions>>().Value.Provider == "Resend"
                ? new ResendEmailSender(provider.GetRequiredKeyedService<HttpClient>("Resend"),
                    provider.GetRequiredService<IOptions<EmailTransportOptions>>())
                : new PickupDirectoryEmailSender(provider.GetRequiredService<IOptions<PickupDirectoryOptions>>(),
                    provider.GetRequiredService<ILogger<PickupDirectoryEmailSender>>()));
        services.AddSingleton(provider => new OutboxDeliveryQueue(provider.GetRequiredService<NpgsqlDataSource>()));
        services.AddSingleton(provider => new TransactionalEmailDispatcher(
            provider.GetRequiredService<OutboxDeliveryQueue>(),
            provider.GetRequiredService<ITransactionalEmailSender>(),
            provider.GetRequiredService<AccountEmails>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<IOptions<OutboxDispatchOptions>>().Value,
            provider.GetRequiredService<ILogger<TransactionalEmailDispatcher>>()));

        return services;
    }
}
