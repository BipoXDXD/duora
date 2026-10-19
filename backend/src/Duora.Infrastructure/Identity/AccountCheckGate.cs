namespace Duora.Infrastructure.Identity;

/// <summary>
/// Fila em memória na frente do lock da conta no banco, com duas formas de chave: por conta (UUID, toda
/// verificação) e por e-mail normalizado (só o login, antes de procurar a conta, para e-mail inexistente também
/// esperar a vez; issue #65). Quem espera outra verificação da mesma chave espera aqui, sem conexão; só quem passa
/// abre a transação. Sem a fila, cada espera segurava uma conexão do pool e 25 logins paralelos de uma conta
/// bastavam para esgotar o <c>max_connections</c> do servidor (53300 → 500).
/// </summary>
/// <remarks>
/// Não substitui o lock no banco, que continua garantindo a exclusão (e vale entre processos). Com uma instância
/// web (ADR-0009) a fila tem efeito pleno; com mais, cada instância deixa passar uma verificação por conta. Entradas
/// saem do dicionário quando ninguém mais as usa. A ordem de aquisição é e-mail → UUID: a vez por UUID fica marcada
/// no fluxo assíncrono de quem a pediu, e <see cref="ThrowIfAccountTurnHeld"/> barra a inversão.
/// </remarks>
internal sealed class AccountCheckGate
{
    private readonly Dictionary<string, Entry> _entries = [];
    private readonly Lock _sync = new();
    private readonly AsyncLocal<AccountTurnMark?> _heldAccountTurn = new();

    /// <summary>Entra na vez da conta (UUID); ocupada por mais que <paramref name="timeout"/>, desiste.</summary>
    /// <remarks>
    /// Não é <c>async</c> de propósito: a marca de vez aberta é gravada no contexto de execução de quem chama, e um
    /// método <c>async</c> descartaria a mudança ao retornar. Pelo mesmo motivo, não encapsule
    /// <c>AccountSecurityService.LockAccountAsync</c> num método <c>async</c> que devolva o check: a marca morre
    /// quando esse método termina, e <see cref="ThrowIfAccountTurnHeld"/> deixa de ver a vez ainda aberta. Chame-o
    /// direto de onde o check é usado e descartado.
    /// </remarks>
    /// <exception cref="AccountBusyException">A espera passou do teto.</exception>
    public Task<IAsyncDisposable> EnterAsync(Guid userId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var held = _heldAccountTurn.Value;
        var mark = new AccountTurnMark(held is { IsHeld: true } ? held : null);
        _heldAccountTurn.Value = mark;
        return EnterAsync($"account:{userId:D}", timeout, mark, cancellationToken);
    }

    /// <summary>Entra na vez de uma chave com namespace (<c>login:</c>); mesmo teto, sem marca de conta.</summary>
    internal Task<IAsyncDisposable> EnterAsync(string key, TimeSpan timeout, CancellationToken cancellationToken)
        => EnterAsync(key, timeout, mark: null, cancellationToken);

    /// <summary>
    /// Falha alto se este fluxo já tem a vez de uma conta: pegar depois a vez por e-mail inverteria a ordem, e duas
    /// requisições esperariam uma à outra até o teto.
    /// </summary>
    /// <exception cref="InvalidOperationException">Há vez por UUID aberta neste fluxo.</exception>
    public void ThrowIfAccountTurnHeld()
    {
        if (_heldAccountTurn.Value is { IsHeld: true })
        {
            throw new InvalidOperationException(
                "Vez por e-mail pedida com a vez da conta aberta: a ordem de aquisição é e-mail → UUID → PostgreSQL.");
        }
    }

    private async Task<IAsyncDisposable> EnterAsync(
        string key, TimeSpan timeout, AccountTurnMark? mark, CancellationToken cancellationToken)
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
            mark?.Release();
            Leave(key, entry, release: false);
            throw;
        }

        return new Turn(this, key, entry, mark);
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

    /// <summary>
    /// Vez por UUID pedida neste fluxo, até ser devolvida. Encadeia só marcas ainda abertas, para vezes de contas
    /// diferentes no mesmo fluxo não se apagarem uma à outra.
    /// </summary>
    private sealed class AccountTurnMark(AccountTurnMark? outer)
    {
        private int _released;

        public bool IsHeld => Volatile.Read(ref _released) == 0 || outer is { IsHeld: true };

        public void Release() => Interlocked.Exchange(ref _released, 1);
    }

    private sealed class Turn(AccountCheckGate gate, string key, Entry entry, AccountTurnMark? mark) : IAsyncDisposable
    {
        private int _left;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _left, 1) == 0)
            {
                mark?.Release();
                gate.Leave(key, entry, release: true);
            }

            return ValueTask.CompletedTask;
        }
    }
}
