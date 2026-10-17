namespace Duora.Infrastructure.Identity;

/// <summary>
/// Fila em memória, por conta, na frente do lock da conta no banco. Quem espera outra verificação da mesma conta
/// espera aqui, sem conexão; só quem passa abre a transação. Sem a fila, cada espera segurava uma conexão do pool e
/// 25 logins paralelos de uma conta bastavam para esgotar o <c>max_connections</c> do servidor (53300 → 500).
/// </summary>
/// <remarks>
/// Não substitui o lock no banco, que continua garantindo a exclusão (e vale entre processos). Com uma instância
/// web (ADR-0009) a fila tem efeito pleno; com mais, cada instância deixa passar uma verificação por conta. Entradas
/// saem do dicionário quando ninguém mais as usa.
/// </remarks>
internal sealed class AccountCheckGate
{
    private readonly Dictionary<string, Entry> _entries = [];
    private readonly Lock _sync = new();

    /// <summary>Entra na vez da conta; ocupada por mais que <paramref name="timeout"/>, desiste.</summary>
    /// <exception cref="AccountBusyException">A espera passou do teto.</exception>
    public async Task<IAsyncDisposable> EnterAsync(Guid userId, TimeSpan timeout, CancellationToken cancellationToken)
        => await EnterAsync($"account:{userId:D}", timeout, cancellationToken);

    internal async Task<IAsyncDisposable> EnterAsync(string key, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Entry entry;
        lock (_sync)
        {
            if (!_entries.TryGetValue(key, out entry!))
            {
                entry = new Entry();
                _entries[key] = entry;
            }

            entry.Users++;
        }

        try
        {
            if (!await entry.Semaphore.WaitAsync(timeout, cancellationToken))
            {
                throw new AccountBusyException(timeout);
            }
        }
        catch
        {
            Leave(key, entry, release: false);
            throw;
        }

        return new Turn(this, key, entry);
    }

    private void Leave(string key, Entry entry, bool release)
    {
        if (release)
        {
            entry.Semaphore.Release();
        }

        lock (_sync)
        {
            entry.Users--;
            if (entry.Users == 0)
            {
                _entries.Remove(key);
                entry.Semaphore.Dispose();
            }
        }
    }

    private sealed class Entry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        /// <summary>Quantos estão na vez ou esperando; protegido por <c>_sync</c>.</summary>
        public int Users { get; set; }
    }

    private sealed class Turn(AccountCheckGate gate, string key, Entry entry) : IAsyncDisposable
    {
        private int _left;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _left, 1) == 0)
            {
                gate.Leave(key, entry, release: true);
            }

            return ValueTask.CompletedTask;
        }
    }
}
