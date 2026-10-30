using System.Collections.Concurrent;
using Duora.Core.Identity;
using Duora.Infrastructure.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// Fornecedor sintético com o resultado de cada leitura em fila: uma entrada por tentativa. Nenhum cliente de SDK,
/// frame ou credencial.
/// </summary>
internal sealed class ScriptedSelfieProvider(FakeTimeProvider time) : ISelfieProvider
{
    public const string Policy = "synthetic-policy";

    private readonly ConcurrentQueue<Func<SelfieResult>> _reads = new();
    private int _readCalls;

    public SelfieCallBarrier? FirstReadBarrier { get; init; }

    public int ReadCalls => Volatile.Read(ref _readCalls);

    public void Then(SelfieState state, string policy = Policy) =>
        _reads.Enqueue(() => new SelfieResult(state, state == SelfieState.Passed ? null : "CAPTURE_QUALITY", policy));

    public void ThenUnavailable() =>
        _reads.Enqueue(() => throw new HttpRequestException("Fornecedor sintético indisponível."));

    public Task<SelfieSession> StartAsync(Guid attemptId, CancellationToken cancellationToken) =>
        Task.FromResult(new SelfieSession(attemptId.ToString("D"), time.GetUtcNow().AddMinutes(3)));

    public async Task<SelfieResult> ReadAsync(string reference, CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _readCalls) == 1 && FirstReadBarrier is not null)
        {
            await FirstReadBarrier.PauseAsync(cancellationToken);
        }

        return _reads.TryDequeue(out var next)
            ? next()
            : throw new InvalidOperationException("Leitura sem resultado roteirizado.");
    }
}

/// <summary>Conduz tentativas inteiras pelo serviço e pelo worker reais, com o relógio do teste.</summary>
internal sealed class SelfieFailureLockoutHarness(string connection, FakeTimeProvider time, ScriptedSelfieProvider provider)
{
    /// <summary>Maior que o intervalo mínimo de 1 minuto entre pedidos (spec 12.2).</summary>
    public static readonly TimeSpan BetweenAttempts = TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1);

    /// <summary>Maior que a espera de 5 s até a próxima leitura do resultado.</summary>
    public static readonly TimeSpan UntilResult = TimeSpan.FromSeconds(6);

    public DuoraWebFactory Host(SelfieCommitFault? fault = null) => new(connection)
    {
        Time = time,
        Settings = SelfieTestSettings.Enabled(ScriptedSelfieProvider.Policy),
        TestServices = services =>
        {
            services.RemoveAll<ISelfieProvider>();
            services.AddSingleton<ISelfieProvider>(provider);
            if (fault is not null)
            {
                services.ConfigureDbContext<Duora.Infrastructure.Persistence.DuoraDbContext>(
                    options => options.AddInterceptors(fault));
            }
        },
    };

    public static async Task<SelfieRequestOutcome> RequestAsync(DuoraWebFactory host, Guid userId)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<SelfieVerificationService>()
            .RequestAsync(userId, TestContext.Current.CancellationToken);
    }

    public static async Task<SelfieStatus> CurrentAsync(DuoraWebFactory host, Guid userId)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<SelfieVerificationService>()
            .GetCurrentAsync(userId, TestContext.Current.CancellationToken);
    }

    public static async Task<int> SuspendPendingAsync(DuoraWebFactory host)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<SelfieAttemptsSuspensionDispatcher>()
            .DispatchBatchAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Uma tentativa completa: pedido, sessão, leitura do resultado roteirizado e a pausa até o próximo pedido.</summary>
    public async Task<Guid> AttemptAsync(DuoraWebFactory host, Guid userId, SelfieState result)
    {
        provider.Then(result);
        var id = Assert.IsType<SelfieRequestOutcome.Accepted>(await RequestAsync(host, userId)).Status.Id!.Value;
        await SelfieConcurrencyHarness.DispatchAsync(host, TestContext.Current.CancellationToken);
        time.Advance(UntilResult);
        await SelfieConcurrencyHarness.DispatchAsync(host, TestContext.Current.CancellationToken);
        time.Advance(BetweenAttempts);
        return id;
    }

    public async Task RejectAsync(DuoraWebFactory host, Guid userId, int times)
    {
        for (var i = 0; i < times; i++)
        {
            await AttemptAsync(host, userId, SelfieState.RecaptureRequired);
        }
    }
}
