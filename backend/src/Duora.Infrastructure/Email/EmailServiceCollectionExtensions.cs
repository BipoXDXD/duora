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
    /// <c>Email:PickupDirectory</c> nem com parâmetros de <c>Outbox</c> incoerentes.
    /// </summary>
    public static IServiceCollection AddTransactionalEmailDispatch(this IServiceCollection services)
    {
        services.AddOptions<PickupDirectoryOptions>()
            .BindConfiguration(PickupDirectoryOptions.Section)
            .Validate(
                o => !string.IsNullOrWhiteSpace(o.PickupDirectory),
                "Email:PickupDirectory é obrigatório (docs/development/environment.md).")
            .ValidateOnStart();

        services.AddOptions<OutboxDispatchOptions>()
            .BindConfiguration(OutboxDispatchOptions.Section)
            .Validate(o => o.IsConsistent(), "Parâmetros de Outbox incoerentes: o lease deve durar mais que o envio.")
            .ValidateOnStart();

        services.AddSingleton<ITransactionalEmailSender, PickupDirectoryEmailSender>();
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
