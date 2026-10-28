using Duora.Core.Media;
using Duora.Infrastructure;
using Duora.Infrastructure.Media;
using Duora.Infrastructure.Persistence;
using Duora.Infrastructure.Safety;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Duora.IntegrationTests.Safety;

public sealed class PhotoReviewCompositionTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PrivateReviewServicesResolveWithoutAdmissionOrAnAutomaticProducer()
    {
        await using var database = await postgres.CreateMigratedScratchDatabaseAsync(Ct);
        await using var provider = Services(database.AppConnectionString).BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;

        _ = services.GetRequiredService<PhotoReviewAccess>();
        _ = services.GetRequiredService<PhotoReviewAssignments>();
        _ = services.GetRequiredService<PhotoReviewEvidence>();
        _ = services.GetRequiredService<PhotoReviewRejections>();
        _ = services.GetRequiredService<PhotoReviewApprovals>();
        _ = services.GetRequiredService<PhotoReviewExpirations>();
        _ = services.GetRequiredService<PhotoReviewCandidateAvailability>();
        _ = services.GetRequiredService<ApprovedMediaBackupCatalog>();
        Assert.Empty(provider.GetServices<IHostedService>());
        Assert.False(provider.GetRequiredService<IOptions<MediaOptions>>().Value.UploadsEnabled);
        Assert.IsType<UnconfiguredObjectStore>(provider.GetRequiredService<IObjectStore>());
    }

    [Fact]
    public async Task PrivateServicesAreScopedAndTheirContextsRemainIndependentlyOwned()
    {
        await using var database = await postgres.CreateMigratedScratchDatabaseAsync(Ct);
        await using var provider = Services(database.AppConnectionString).BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using var firstScope = provider.CreateAsyncScope();
        await using var secondScope = provider.CreateAsyncScope();
        var first = firstScope.ServiceProvider.GetRequiredService<PhotoReviewEvidence>();
        Assert.Same(first, firstScope.ServiceProvider.GetRequiredService<PhotoReviewEvidence>());
        Assert.NotSame(first, secondScope.ServiceProvider.GetRequiredService<PhotoReviewEvidence>());

        var factory = firstScope.ServiceProvider.GetRequiredService<IDbContextFactory<DuoraDbContext>>();
        await using var second = await factory.CreateDbContextAsync(Ct);
        await using (var owned = await factory.CreateDbContextAsync(Ct))
        {
            Assert.NotSame(owned, second);
            Assert.NotSame(owned, firstScope.ServiceProvider.GetRequiredService<DuoraDbContext>());
            Assert.Equal(1, await owned.Database.SqlQuery<int>($"select 1 as \"Value\"").SingleAsync(Ct));
        }
        Assert.Equal(1, await second.Database.SqlQuery<int>($"select 1 as \"Value\"").SingleAsync(Ct));
        await using var borrowed = await provider.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync(Ct);
        await using var command = new NpgsqlCommand("select 1", borrowed);
        Assert.Equal(1, await command.ExecuteScalarAsync(Ct));
    }

    [Fact]
    public async Task OwnedContextsBorrowTheExistingSingleConnectionPoolInsteadOfCreatingAnother()
    {
        await using var database = await postgres.CreateMigratedScratchDatabaseAsync(Ct);
        await using var provider = Services(database.AppConnectionString, maxPoolSize: 1).BuildServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<DuoraDbContext>>();
        var source = provider.GetRequiredService<NpgsqlDataSource>();
        await using var owned = await factory.CreateDbContextAsync(Ct);
        await using (var held = await source.OpenConnectionAsync(Ct))
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            deadline.CancelAfter(TimeSpan.FromMilliseconds(500));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owned.Database.OpenConnectionAsync(deadline.Token));
        }
        await owned.Database.OpenConnectionAsync(Ct);
        Assert.Equal(1, await owned.Database.SqlQuery<int>($"select 1 as \"Value\"").SingleAsync(Ct));
        await owned.Database.CloseConnectionAsync();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => factory.CreateDbContextAsync(cancelled.Token));
    }

    private static IServiceCollection Services(string connectionString, int maxPoolSize = 2)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Duora"] = connectionString,
            ["Database:MaxPoolSize"] = maxPoolSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Duora:PublicOrigin"] = "https://synthetic.example.test",
            ["Duora:SupportContact"] = "support@example.test",
        }).Build();
        return new ServiceCollection().AddLogging().AddSingleton(TimeProvider.System)
            .AddInfrastructure(configuration, ConnectionShare.Web).AddMediaUploads(configuration, "Testing");
    }
}
