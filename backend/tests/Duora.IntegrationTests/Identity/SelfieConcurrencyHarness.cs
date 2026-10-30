using System.Collections.Concurrent;
using System.Data.Common;
using Duora.Core.Identity;
using Duora.Infrastructure.Identity;
using Duora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace Duora.IntegrationTests.Identity;

// Two independent hosts share only the PostgreSQL database, clock and simulated external system.
internal static class SelfieConcurrencyHarness
{
    public static DuoraWebFactory Host(string connection, FakeTimeProvider time, SimulatedSelfieProvider provider,
        SelfieCommitFault? fault = null) => new(connection)
        {
            Time = time,
            Settings = SelfieTestSettings.Enabled("synthetic-policy"),
            TestServices = services =>
            {
                services.RemoveAll<ISelfieProvider>();
                services.AddSingleton<ISelfieProvider>(provider);
                if (fault is not null)
                {
                    services.ConfigureDbContext<DuoraDbContext>(options => options.AddInterceptors(fault));
                }
            },
        };

    public static async Task<int> DispatchAsync(DuoraWebFactory host, CancellationToken cancellationToken)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<SelfieVerificationDispatcher>().DispatchBatchAsync(cancellationToken);
    }

    public static async Task<SelfieStatus> CurrentAsync(DuoraWebFactory host, Guid userId, CancellationToken cancellationToken)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<SelfieVerificationService>().GetCurrentAsync(userId, cancellationToken);
    }

    public static async Task<bool> EligibleAsync(DuoraWebFactory host, Guid userId, CancellationToken cancellationToken)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<SelfieVerificationService>().GetEligibilityAsync(userId, cancellationToken);
    }

    public static async Task RequestAsync(DuoraWebFactory host, Guid userId, CancellationToken cancellationToken)
    {
        using var scope = host.Services.CreateScope();
        Assert.IsType<SelfieRequestOutcome.Accepted>(await scope.ServiceProvider.GetRequiredService<SelfieVerificationService>().RequestAsync(userId, cancellationToken));
    }
}

internal sealed class SelfieCallBarrier
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task WaitForEntryAsync(CancellationToken cancellationToken) => _entered.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
    public void Release() => _release.TrySetResult();

    public async Task PauseAsync(CancellationToken cancellationToken)
    {
        _entered.TrySetResult();
        await _release.Task.WaitAsync(cancellationToken);
    }
}

// Models external idempotency by attempt ID. It never creates an SDK client, frame or credential.
internal sealed class SimulatedSelfieProvider(FakeTimeProvider time) : ISelfieProvider
{
    private readonly ConcurrentDictionary<Guid, SelfieSession> _sessions = new();
    private int _starts;
    private int _reads;
    private int _startResponses;
    private int _readResponses;

    public SelfieCallBarrier? FirstStartBarrier { get; init; }
    public SelfieCallBarrier? FirstReadBarrier { get; init; }
    public bool LoseFirstStartResponse { get; init; }
    public SelfieState SubsequentReadResult { get; init; } = SelfieState.Passed;
    public int StartCalls => Volatile.Read(ref _starts);
    public int ReadCalls => Volatile.Read(ref _reads);
    public int Sessions => _sessions.Count;
    public int StartResponses => Volatile.Read(ref _startResponses);
    public int ReadResponses => Volatile.Read(ref _readResponses);

    public async Task<SelfieSession> StartAsync(Guid attemptId, CancellationToken cancellationToken)
    {
        var call = Interlocked.Increment(ref _starts);
        var session = _sessions.GetOrAdd(attemptId, _ => new(Guid.NewGuid().ToString(), time.GetUtcNow().AddMinutes(3)));
        if (call == 1 && FirstStartBarrier is not null)
        {
            await FirstStartBarrier.PauseAsync(cancellationToken);
        }
        if (call == 1 && LoseFirstStartResponse)
        {
            throw new HttpRequestException("Simulated lost response after session creation.");
        }
        Interlocked.Increment(ref _startResponses);
        return session;
    }

    public async Task<SelfieResult> ReadAsync(string reference, CancellationToken cancellationToken)
    {
        if (!_sessions.Values.Any(session => session.Reference == reference))
        {
            throw new InvalidDataException("Unexpected synthetic session.");
        }
        var call = Interlocked.Increment(ref _reads);
        if (call == 1 && FirstReadBarrier is not null)
        {
            await FirstReadBarrier.PauseAsync(cancellationToken);
        }
        Interlocked.Increment(ref _readResponses);
        var state = call == 1 ? SelfieState.Passed : SubsequentReadResult;
        return new(state, state == SelfieState.Passed ? null : "PRESENCE_INCONCLUSIVE", "synthetic-policy");
    }
}

internal sealed class SelfieSimulatedCrash : Exception;

// EF transaction boundaries only; the real queue continues to use its independent Npgsql transactions.
internal sealed class SelfieCommitFault(bool afterCommit) : DbTransactionInterceptor
{
    private int _armed;
    public bool Fired { get; private set; }
    public void Arm() => Volatile.Write(ref _armed, 1);

    public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
        TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        if (!afterCommit)
        {
            FailOnce();
        }
        return ValueTask.FromResult(result);
    }

    public override Task TransactionCommittedAsync(DbTransaction transaction,
        TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        if (afterCommit)
        {
            FailOnce();
        }
        return Task.CompletedTask;
    }

    private void FailOnce()
    {
        if (Interlocked.Exchange(ref _armed, 0) == 1)
        {
            Fired = true;
            throw new SelfieSimulatedCrash();
        }
    }
}
