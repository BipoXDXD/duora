using System.Globalization;
using System.Net;
using Duora.IntegrationTests.Identity;
using Npgsql;

namespace Duora.IntegrationTests.Media;

/// <summary>
/// Teto global da fila de mídia (spec 11.5, TM-43): o teto por conta não limita quem tem várias contas, e o Worker
/// processa uma foto por vez. Acima do teto a recepção recusa com 503 e Retry-After (shed load), sem gravar nada.
/// Banco próprio, porque a contagem é de todos os donos.
/// </summary>
[Trait("Acceptance", "T-16")]
[Collection(nameof(UploadQueueLimitRunsAlone))]
public sealed class UploadQueueLimitTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const int QueueCap = 3;

    private string _connectionString = "";
    private DuoraWebFactory? _factory;
    private MediaScenario? _media;

    private MediaScenario Media => _media ?? throw new InvalidOperationException("Teste não inicializado.");

    public async ValueTask InitializeAsync()
    {
        _connectionString = await postgres.CreateMigratedDatabaseAsync(TestContext.Current.CancellationToken);
        _factory = new DuoraWebFactory(_connectionString)
        {
            Settings = new Dictionary<string, string?> { ["Media:MaxQueuedUploads"] = QueueCap.ToString(CultureInfo.InvariantCulture) },
        };
        _media = new MediaScenario(_connectionString, _factory);
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }
    }

    [Fact]
    public async Task UploadsFromManyAccountsStopAtTheQueueCap()
    {
        var accepted = 0;
        for (var i = 0; i < QueueCap; i++)
        {
            using var browser = await NewBrowserAsync();
            using var response = await MediaScenario.UploadAsync(browser, MediaScenario.JpegLike(fill: (byte)i));
            accepted += response.StatusCode == HttpStatusCode.Accepted ? 1 : 0;
        }

        using var late = await NewBrowserAsync();
        using var refused = await MediaScenario.UploadAsync(late, MediaScenario.JpegLike(fill: 9));

        Assert.Equal(QueueCap, accepted);
        await ProblemAssert.IsProblemAsync(refused, HttpStatusCode.ServiceUnavailable, "MEDIA_QUEUE_FULL");
        ProblemAssert.RetryAfterSeconds(refused);
        Assert.Equal(QueueCap, await CountUploadsAsync());
    }

    [Fact]
    public async Task ConcurrentUploadsFromManyAccountsNeverPassTheQueueCap()
    {
        // Contas diferentes não se esperam na guarda do dono; a contagem global precisa da própria serialização.
        const int Attempts = QueueCap * 3;
        var browsers = new List<Browser>();
        for (var i = 0; i < Attempts; i++)
        {
            browsers.Add(await NewBrowserAsync());
        }

        // Largada única: os 9 envios aguardam o mesmo sinal sem prender thread do pool e partem juntos no SetResult.
        // RunContinuationsAsynchronously evita que o SetResult rode o início dos 9 envios na própria thread do teste.
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var uploads = browsers.Select(async (browser, i) =>
        {
            await start.Task;
            return await MediaScenario.UploadAsync(browser, MediaScenario.JpegLike(fill: (byte)i));
        }).ToList();
        start.SetResult();
        var responses = await Task.WhenAll(uploads);

        try
        {
            Assert.Equal(QueueCap, responses.Count(r => r.StatusCode == HttpStatusCode.Accepted));
            Assert.All(responses, r => Assert.True(
                r.StatusCode is HttpStatusCode.Accepted or HttpStatusCode.ServiceUnavailable,
                $"Status inesperado {(int)r.StatusCode}."));
            Assert.Equal(QueueCap, await CountUploadsAsync());
        }
        finally
        {
            Array.ForEach(responses, r => r.Dispose());
            browsers.ForEach(b => b.Dispose());
        }
    }

    [Fact]
    public async Task ADecidedUploadLeavesTheQueue()
    {
        for (var i = 0; i < QueueCap; i++)
        {
            using var browser = await NewBrowserAsync();
            using var response = await MediaScenario.UploadAsync(browser, MediaScenario.JpegLike(fill: (byte)i));
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }

        await Media.ExecuteAsync(
            "update media.uploads set state = 'Rejected', rejection_code = 'CORRUPT_IMAGE' where id = (select id from media.uploads order by created_at limit 1)");
        using var next = await NewBrowserAsync();
        using var accepted = await MediaScenario.UploadAsync(next, MediaScenario.JpegLike(fill: 9));

        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
    }

    private async Task<Browser> NewBrowserAsync() =>
        await Media.Identity.SignedInBrowserAsync(await Media.Identity.CreateConfirmedAccountAsync());

    private async Task<long> CountUploadsAsync()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("select count(*) from media.uploads", connection);
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
}

/// <summary>
/// Os envios simultâneos deste teste disputam o thread pool e o banco com o resto da suíte; rodar a classe sozinha
/// isola o pico sem tirar a concorrência entre os envios dentro do teste.
/// </summary>
[CollectionDefinition(nameof(UploadQueueLimitRunsAlone), DisableParallelization = true)]
public sealed class UploadQueueLimitRunsAlone;
