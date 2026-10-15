using Duora.Core.Notifications;

namespace Duora.IntegrationTests.Outbox;

/// <summary>Falha nas primeiras <paramref name="failures"/> chamadas e depois delega ao envio real.</summary>
internal sealed class FailingSender(ITransactionalEmailSender inner, int failures) : ITransactionalEmailSender
{
    private int _calls;

    public Task SendAsync(TransactionalEmail email, string idempotencyKey, CancellationToken cancellationToken) =>
        Interlocked.Increment(ref _calls) <= failures
            ? Task.FromException(new IOException("Provedor indisponível (simulado)."))
            : inner.SendAsync(email, idempotencyKey, cancellationToken);
}

/// <summary>
/// Trava o primeiro envio até o teste liberar, ignorando o cancelamento: simula um processo pausado (GC, VM
/// congelada) ou um SDK que não coopera, exatamente o caso em que o lease vence com o envio em andamento.
/// </summary>
internal sealed class StalledSender(ITransactionalEmailSender inner) : ITransactionalEmailSender
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Entered => _entered.Task;

    public void Release() => _released.TrySetResult();

    public async Task SendAsync(TransactionalEmail email, string idempotencyKey, CancellationToken cancellationToken)
    {
        _entered.TrySetResult();
        await _released.Task;
        await inner.SendAsync(email, idempotencyKey, CancellationToken.None);
    }
}
